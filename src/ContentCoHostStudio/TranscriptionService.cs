using System.Diagnostics;
using System.Text.Json;
using Vosk;

namespace ContentCoHostStudio;

public sealed class TranscriptionService(string? modelDirectory = null, IMicrophoneSource? microphone = null)
{
    private readonly string modelDirectory = modelDirectory ?? ModelPath;
    private readonly IMicrophoneSource microphone = microphone ?? new MicrophoneCaptureService();
    private CancellationTokenSource? stopSource;
    private Task sessionTask = Task.CompletedTask;
    private int requestedGeneration;
    private long resetTimestamp;

    public event Action<int, string, bool>? Caption;
    public event Action<string>? Status;
    public event Action<string>? Error;
    public bool IsRunning => !sessionTask.IsCompleted;
    public static string ModelPath => Environment.GetEnvironmentVariable("CONTENT_COHOST_VOSK_MODEL")
        ?? Path.Combine(AppContext.BaseDirectory, "models", "vosk-model-small-en-us-0.15");

    public void Start(int generation)
    {
        if (IsRunning)
            return;
        stopSource?.Dispose();
        stopSource = new CancellationTokenSource();
        requestedGeneration = generation;
        Interlocked.Exchange(ref resetTimestamp, Stopwatch.GetTimestamp());
        Status?.Invoke("Starting…");
        CancellationToken token = stopSource.Token;
        sessionTask = Task.Run(() => RunSessionAsync(token));
    }

    public void Reset(int generation)
    {
        Interlocked.Exchange(ref resetTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref requestedGeneration, generation);
    }

    public async Task StopAsync()
    {
        if (IsRunning)
        {
            Status?.Invoke("Stopping…");
            stopSource?.Cancel();
            await sessionTask;
        }
        stopSource?.Dispose();
        stopSource = null;
    }

    private async Task RunSessionAsync(CancellationToken token)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
            if (!File.Exists(Path.Combine(modelDirectory, "am", "final.mdl")))
                throw new DirectoryNotFoundException();
            Vosk.Vosk.SetLogLevel(-1);
            using var model = new Model(modelDirectory);
            token.ThrowIfCancellationRequested();
            using var recognizer = new VoskRecognizer(model, AudioConverter.RecognitionSampleRate);
            int activeGeneration = Volatile.Read(ref requestedGeneration);
            void SynchronizeReset()
            {
                int requested = Volatile.Read(ref requestedGeneration);
                if (requested == activeGeneration)
                    return;
                recognizer.Reset();
                activeGeneration = requested;
            }
            void Process(AudioPacket packet)
            {
                SynchronizeReset();
                if (packet.CapturedAt < Interlocked.Read(ref resetTimestamp) || packet.Bytes.Length == 0)
                    return;
                bool final = recognizer.AcceptWaveform(packet.Bytes, packet.Bytes.Length);
                Caption?.Invoke(activeGeneration, ReadText(final ? recognizer.Result() : recognizer.PartialResult(), final ? "text" : "partial"), final);
            }
            var subscription = await microphone.SubscribeAsync(token).ConfigureAwait(false);
            Status?.Invoke("Listening");
            try
            {
                await foreach (var packet in subscription.Audio.ReadPacketsAsync(token).ConfigureAwait(false))
                    Process(packet);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { await subscription.DisposeAsync().ConfigureAwait(false); }
            await foreach (var packet in subscription.Audio.ReadPacketsAsync(CancellationToken.None).ConfigureAwait(false))
                Process(packet);
            SynchronizeReset();
            Caption?.Invoke(activeGeneration, ReadText(recognizer.FinalResult(), "text"), true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (DirectoryNotFoundException)
        {
            Error?.Invoke("The Vosk English model is missing. Follow README.md or set CONTENT_COHOST_VOSK_MODEL.");
        }
        catch (PlatformNotSupportedException)
        {
            Error?.Invoke("Audio capture in this POC supports Windows only.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
        {
            Error?.Invoke("Vosk's native library could not load. Use Windows x64 and follow README.md.");
        }
        catch (Exception)
        {
            Error?.Invoke("Transcription stopped. Check the Vosk model, selected audio device, Windows microphone permissions, and system load. Refresh devices if an input was disconnected; audio buffer overflow also stops capture.");
        }
        finally { Status?.Invoke("Stopped"); }
    }

    private static string ReadText(string json, string field)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(field, out var text) ? text.GetString() ?? "" : "";
    }
}
