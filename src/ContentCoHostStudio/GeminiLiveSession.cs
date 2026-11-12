using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContentCoHostStudio;

public sealed class LiveSessionException(string message) : Exception(message);
public enum LiveSessionState { Connecting, Listening, Responding, Closed, TimedOut, Failed }
public enum LiveReplyLength { OneWord, OneSentence, Custom }
public sealed record LiveSessionOptions(bool UseMicrophone, string Personality, string Voice, string VoiceStyle, string Behavior,
    string Model = GeminiLiveSession.Model, int DurationSeconds = 30, LiveReplyLength ReplyLength = LiveReplyLength.OneSentence, int MaximumWords = 20)
{
    public string BrevityInstruction => "Reply length (takes precedence over any conflicting personality or behavior length instructions): " + (ReplyLength switch
    {
        LiveReplyLength.OneWord => "Use exactly one spoken word per reply. No introductions or explanations.",
        LiveReplyLength.OneSentence => "Use exactly one short sentence per reply. No introductions or follow-up sentences.",
        _ => $"Use no more than {MaximumWords} spoken words per reply."
    });
    public void Validate()
    {
        if (DurationSeconds is < 1 or > 600 || MaximumWords is < 1 or > 100 || !Enum.IsDefined(ReplyLength))
            throw new InvalidOperationException("Live duration must be 1–600 seconds and custom reply length 1–100 words.");
    }
}

public sealed class LiveResponseTranscript
{
    private string completed = "";
    private string current = "";
    public string Text => completed + current;
    public void Append(string text) => current += text;
    public void FinishTurn(string? marker = null)
    {
        if (current.Length == 0)
            return;
        completed += current.Trim() + (marker == null ? "" : $" [{marker}]") + Environment.NewLine;
        current = "";
    }
}

public sealed class GeminiLiveSession(ILiveTransport transport, ILiveAudioPlayback playback, IMicrophoneSource microphone,
    TimeSpan? sessionLimit = null, TimeProvider? clock = null) : IAsyncDisposable
{
    public const string Model = "gemini-3.8-live";
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(10);
    public const string DefaultBehavior = "Stay quiet during normal narration, breaths, and brief pauses. " +
        "Respond only when directly addressed or when a clear stumble offers an appropriate brief joke. " +
        "Prefer one short sentence, then return to listening. Yield immediately when the creator resumes speaking. " +
        "Do not greet the creator or comment just because the session starts.";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private TimeSpan duration;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly CancellationTokenSource stopSource = new();
    private readonly LiveResponseTranscript transcript = new();
    private readonly object outputGate = new();
    private Task runTask = Task.CompletedTask;
    private CancellationToken sessionToken;
    private LiveSessionOptions? options;
    private volatile LiveSessionState state = LiveSessionState.Closed;
    private volatile bool ready;
    private volatile bool turnPending;
    private volatile bool initialTurnSent;
    private long startedAt;
    private long endedAt = -1;
    private long connectedTicks = -1;
    private long firstAudioTicks = -1;
    private long textSentAt = -1;
    private int started;
    private int disposed;

    public event Action? Changed;
    public event Action<string>? ResponseChanged;
    public event Action<string>? Error;
    public event Action<LivePlaybackTurn>? PlaybackChanged;
    public bool IsActive => Volatile.Read(ref started) != 0 && Interlocked.Read(ref endedAt) < 0 && !stopSource.IsCancellationRequested && !sessionToken.IsCancellationRequested && Elapsed < duration;
    public bool IsMicrophone => options?.UseMicrophone == true;
    public string SessionModel => options?.Model ?? Model;
    public bool CanSendText => ready && initialTurnSent && IsActive && !IsMicrophone && !turnPending && playback.BufferedBytes == 0 && !playback.IsSpeaking;
    public LiveSessionState State => state == LiveSessionState.Listening && (playback.BufferedBytes > 0 || playback.IsSpeaking) ? LiveSessionState.Responding : state;
    public TimeSpan Duration => duration;
    public TimeSpan Elapsed => Volatile.Read(ref started) == 0 ? TimeSpan.Zero : clock.GetElapsedTime(startedAt, endedAt < 0 ? clock.GetTimestamp() : endedAt);
    public TimeSpan? ConnectionTime => Interlocked.Read(ref connectedTicks) is var ticks && ticks >= 0 ? TimeSpan.FromTicks(ticks) : null;
    public TimeSpan? FirstAudioTime => Interlocked.Read(ref firstAudioTicks) is var ticks && ticks >= 0 ? TimeSpan.FromTicks(ticks) : null;
    public string ResponseText { get { lock (outputGate) return transcript.Text; } }

    public static JsonObject BuildSetup(LiveSessionOptions options)
    {
        var message = new JsonObject
        {
            ["setup"] = new JsonObject
            {
                ["model"] = "models/" + options.Model,
                ["generationConfig"] = new JsonObject
                {
                    ["responseModalities"] = new JsonArray("AUDIO"),
                    ["speechConfig"] = new JsonObject
                    {
                        ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = options.Voice } }
                    }
                },
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject
                    {
                        ["text"] = options.Personality + "\nVoice delivery: " + options.VoiceStyle + "\nLive behavior: " + options.Behavior +
                        (options.UseMicrophone ? "" : "\nEvery saved-transcript text turn is an explicit request for one brief spoken co-host reply.") +
                        "\n" + options.BrevityInstruction
                    })
                },
                ["outputAudioTranscription"] = new JsonObject(),
                ["realtimeInputConfig"] = new JsonObject
                {
                    ["automaticActivityDetection"] = new JsonObject { ["disabled"] = false },
                    ["activityHandling"] = "START_OF_ACTIVITY_INTERRUPTS"
                }
            }
        };
        // 3.8 is always proactive; 3.1 rejects this option. Only 2.5 needs it enabled explicitly.
        if (GeminiModelCatalog.GetLiveProtocol(options.Model) == LiveProtocol.ProactiveNativeAudio)
            message["setup"]!["proactivity"] = new JsonObject { ["proactiveAudio"] = true };
        return message;
    }

    public Task RunAsync(string apiKey, LiveSessionOptions settings, string initialTranscript, CancellationToken cancellationToken)
    {
        settings.Validate();
        duration = sessionLimit ?? TimeSpan.FromSeconds(settings.DurationSeconds);
        if (duration <= TimeSpan.Zero || duration > MaximumDuration)
            throw new InvalidOperationException("Live sessions must be limited to at most ten minutes.");
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("Create a fresh Live session for each Start.");
        options = settings;
        startedAt = clock.GetTimestamp();
        playback.TurnChanged += ForwardPlayback;
        playback.Failed += PlaybackFailed;
        state = LiveSessionState.Connecting;
        runTask = Task.Run(() => RunCoreAsync(apiKey, initialTranscript, cancellationToken));
        return runTask;
    }

    private async Task RunCoreAsync(string apiKey, string initialTranscript, CancellationToken cancellationToken)
    {
        TimeSpan remaining = duration - clock.GetElapsedTime(startedAt);
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, clock);
        if (remaining <= TimeSpan.Zero) deadline.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopSource.Token, deadline.Token);
        sessionToken = linked.Token;
        using var abortAtDeadline = deadline.Token.Register(() =>
        {
            try { transport.Abort(); } catch (Exception) { }
            SafeInterrupt();
        });
        Task audioTask = Task.CompletedTask;
        Task<string>? pendingReceive = null;
        try
        {
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl))
                throw new LiveSessionException("Enter a valid Gemini API key before starting Live.");
            if (!GeminiSpeechService.Voices.Contains(options!.Voice))
                throw new LiveSessionException("Choose a supported voice before starting Live.");
            if (GeminiModelCatalog.GetLiveProtocol(options.Model) == LiveProtocol.Unsupported)
                throw new LiveSessionException("Choose a supported Gemini Live model before starting. No fallback model was used.");
            sessionToken.ThrowIfCancellationRequested();
            await transport.ConnectAsync(apiKey.Trim(), sessionToken).ConfigureAwait(false);
            await SendJsonAsync(BuildSetup(options), sessionToken).ConfigureAwait(false);
            // Nothing is uploaded before the server acknowledges setup.
            while (!ready)
                ProcessMessage(await ReceiveMessageAsync(sessionToken).ConfigureAwait(false));
            Interlocked.Exchange(ref connectedTicks, clock.GetElapsedTime(startedAt).Ticks);
            if (options.UseMicrophone)
                audioTask = StreamMicrophoneAsync(sessionToken);
            else
            {
                await SendTextCoreAsync(initialTranscript, sessionToken, initial: true).ConfigureAwait(false);
                initialTurnSent = true;
            }
            while (true)
            {
                pendingReceive = ReceiveMessageAsync(sessionToken);
                if (options.UseMicrophone && await Task.WhenAny(pendingReceive, audioTask).ConfigureAwait(false) == audioTask)
                {
                    await audioTask.ConfigureAwait(false);
                    throw new LiveSessionException("The Live audio input stream ended unexpectedly.");
                }
                ProcessMessage(await pendingReceive.ConfigureAwait(false));
            }
        }
        catch (Exception exception)
        {
            if (deadline.IsCancellationRequested)
                state = LiveSessionState.TimedOut;
            else if (cancellationToken.IsCancellationRequested || stopSource.IsCancellationRequested)
                state = LiveSessionState.Closed;
            else
            {
                state = LiveSessionState.Failed;
                Error?.Invoke(exception is LiveSessionException ? exception.Message :
                    "Live could not connect or continue. Check your key, model access, network, selected input, and output device. No automatic reconnect was attempted.");
            }
        }
        finally
        {
            ready = false;
            linked.Cancel();
            SafeInterrupt();
            try { await audioTask.ConfigureAwait(false); } catch (Exception) { }
            if (deadline.IsCancellationRequested)
                transport.Abort();
            else
            {
                using var closeLimit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await transport.CloseAsync(closeLimit.Token).WaitAsync(closeLimit.Token).ConfigureAwait(false); }
                catch (Exception) { transport.Abort(); }
            }
            if (pendingReceive != null)
            {
                try { await pendingReceive.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (Exception) { }
            }
            lock (outputGate)
            {
                transcript.FinishTurn(state == LiveSessionState.TimedOut ? "timed out / incomplete" : "incomplete");
                ResponseChanged?.Invoke(transcript.Text);
            }
            Interlocked.Exchange(ref endedAt, clock.GetTimestamp());
            Changed?.Invoke();
        }
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken) => SendTextCoreAsync(text, cancellationToken, initial: false);

    private async Task SendTextCoreAsync(string text, CancellationToken cancellationToken, bool initial)
    {
        if (!(initial ? ready && IsActive && !IsMicrophone : CanSendText) || string.IsNullOrWhiteSpace(text))
            throw new LiveSessionException("Wait for the reply to finish, and enter a saved transcript before sending.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionToken);
        turnPending = true;
        state = LiveSessionState.Responding;
        Interlocked.Exchange(ref textSentAt, clock.GetTimestamp());
        Interlocked.Exchange(ref firstAudioTicks, -1);
        Changed?.Invoke();
        await SendJsonAsync(new JsonObject
        {
            ["clientContent"] = new JsonObject
            {
                ["turns"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) }),
                ["turnComplete"] = true
            }
        }, linked.Token).ConfigureAwait(false);
    }

    private async Task StreamMicrophoneAsync(CancellationToken token)
    {
        await using var subscription = await microphone.SubscribeAsync(token).ConfigureAwait(false);
        try
        {
            await foreach (byte[] pcm in subscription.Audio.ReadAllAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                await SendJsonAsync(new JsonObject
                {
                    ["realtimeInput"] = new JsonObject
                    {
                        ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(pcm), ["mimeType"] = "audio/pcm;rate=16000" }
                    }
                }, token).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            throw new LiveSessionException("Live audio capture stopped or its two-second buffer filled. Check the selected device and network, then start a new session.");
        }
    }

    private async Task SendJsonAsync(JsonObject message, CancellationToken token)
    {
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            await transport.SendAsync(message.ToJsonString(), token).ConfigureAwait(false);
        }
        finally { sendGate.Release(); }
    }

    private async Task<string> ReceiveMessageAsync(CancellationToken token)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await transport.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new LiveSessionException("The Live server closed the connection. Start a new session when ready.");
            if (message.Length + result.Count > 4 * 1024 * 1024)
                throw new LiveSessionException("Live returned an unsupported or oversized message. The session was stopped.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return System.Text.Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }

    private void ProcessMessage(string json)
    {
        sessionToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _) || root.TryGetProperty("goAway", out _))
            throw new LiveSessionException("Live is unavailable or the server requested disconnection. Check access/quota and start a new session later.");
        if (root.TryGetProperty("setupComplete", out _))
        {
            ready = true;
            state = LiveSessionState.Listening;
        }
        if (!root.TryGetProperty("serverContent", out var content))
        {
            Changed?.Invoke();
            return;
        }
        lock (outputGate)
        {
            if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.GetBoolean())
            {
                playback.Interrupt();
                transcript.FinishTurn("interrupted");
                turnPending = false;
                state = LiveSessionState.Listening;
                ResponseChanged?.Invoke(transcript.Text);
                Changed?.Invoke();
                return; // Discard any cancelled audio included in this same event.
            }
            string text = "";
            if (content.TryGetProperty("modelTurn", out var turn) && turn.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("inlineData", out var audio))
                    {
                        string mime = audio.GetProperty("mimeType").GetString() ?? "";
                        if (!mime.StartsWith("audio/pcm", StringComparison.OrdinalIgnoreCase) || !mime.Contains("rate=24000", StringComparison.OrdinalIgnoreCase))
                            throw new LiveSessionException("Live returned an unsupported audio format.");
                        byte[] pcm = Convert.FromBase64String(audio.GetProperty("data").GetString() ?? "");
                        if (pcm.Length == 0 || pcm.Length % 2 != 0)
                            throw new LiveSessionException("Live returned an incomplete PCM audio sample.");
                        sessionToken.ThrowIfCancellationRequested();
                        playback.Append(pcm);
                        if (!IsMicrophone && Interlocked.Read(ref firstAudioTicks) < 0 && Interlocked.Read(ref textSentAt) >= 0)
                            Interlocked.Exchange(ref firstAudioTicks, clock.GetElapsedTime(textSentAt).Ticks);
                        state = LiveSessionState.Responding;
                    }
                    if (part.TryGetProperty("text", out var partText) &&
                        !(part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True))
                        text += partText.GetString();
                }
            }
            if (content.TryGetProperty("outputTranscription", out var outputText))
                text = outputText.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
            transcript.Append(text);
            if (content.TryGetProperty("turnComplete", out var complete) && complete.GetBoolean())
            {
                playback.CompleteTurn();
                transcript.FinishTurn();
                turnPending = false;
                state = LiveSessionState.Listening;
            }
            ResponseChanged?.Invoke(transcript.Text);
        }
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        stopSource.Cancel();
        await Task.Run(SafeInterrupt).ConfigureAwait(false);
        await runTask.ConfigureAwait(false);
    }

    private void SafeInterrupt()
    {
        // Serialize stop with decoding/appending so a cancellation cannot restart late audio.
        lock (outputGate)
        {
            try { playback.Interrupt(); } catch (Exception) { }
        }
    }

    private void ForwardPlayback(LivePlaybackTurn turn)
    {
        if (!sessionToken.IsCancellationRequested) PlaybackChanged?.Invoke(turn);
    }
    private void PlaybackFailed() { try { transport.Abort(); } catch (Exception) { } }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        playback.TurnChanged -= ForwardPlayback;
        playback.Failed -= PlaybackFailed;
        try { await Task.Run(playback.Dispose).ConfigureAwait(false); }
        finally
        {
            try { await transport.DisposeAsync().ConfigureAwait(false); }
            finally { stopSource.Dispose(); sendGate.Dispose(); }
        }
    }
}
