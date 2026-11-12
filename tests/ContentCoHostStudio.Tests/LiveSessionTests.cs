using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class LiveSessionTests
{
    internal static LiveSessionOptions Options(bool microphone = false) => new(microphone, "Friendly robot personality", "Puck",
        GeminiSpeechService.DroidStyle, GeminiLiveSession.DefaultBehavior);
    internal static string AudioEvent(string text = "Hello", bool complete = true) => new JsonObject
    {
        ["serverContent"] = new JsonObject
        {
            ["modelTurn"] = new JsonObject
            {
                ["parts"] = new JsonArray(AudioPart(), AudioPart(), new JsonObject { ["text"] = "duplicate text" })
            },
            ["outputTranscription"] = new JsonObject { ["text"] = text },
            ["turnComplete"] = complete
        }
    }.ToJsonString();
    private static JsonObject AudioPart() => new()
    {
        ["inlineData"] = new JsonObject { ["mimeType"] = "audio/pcm;rate=24000", ["data"] = "AQIDBA==" }
    };

    [Fact]
    public void SetupUsesNativeAudioVoiceInstructionsAndVadWithoutUnsupportedFeatures()
    {
        using var document = JsonDocument.Parse(GeminiLiveSession.BuildSetup(Options(true)).ToJsonString());
        var setup = document.RootElement.GetProperty("setup");
        Assert.Equal("models/gemini-3.8-live", setup.GetProperty("model").GetString());
        var generation = setup.GetProperty("generationConfig");
        Assert.Equal("AUDIO", generation.GetProperty("responseModalities")[0].GetString());
        Assert.Equal("Puck", generation.GetProperty("speechConfig").GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
        string instructions = setup.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains(Options().Personality, instructions);
        Assert.Contains(GeminiSpeechService.DroidStyle, instructions);
        Assert.Contains(GeminiLiveSession.DefaultBehavior, instructions);
        Assert.True(setup.TryGetProperty("outputAudioTranscription", out _));
        Assert.False(setup.GetProperty("realtimeInputConfig").GetProperty("automaticActivityDetection").GetProperty("disabled").GetBoolean());
        foreach (string unsupported in new[] { "tools", "sessionResumption", "thinkingConfig", "proactivity" })
            Assert.DoesNotContain(unsupported, setup.GetRawText());
        Assert.Equal(TimeSpan.FromMinutes(10), GeminiLiveSession.MaximumDuration);
    }

    [Fact]
    public async Task SelectedLiveModelsUseTheirOwnSetupAndLegacyProactivity()
    {
        foreach (string model in new[] { GeminiLiveSession.Model, "gemini-3.1-flash-live-preview", "gemini-2.5-flash-native-audio-preview-12-2025" })
        {
            var transport = new FakeLiveTransport();
            await using var session = new GeminiLiveSession(transport, new SilentLivePlayback(), new FakeLiveMicrophone());
            _ = session.RunAsync("fake-key", Options() with { Model = model }, "Transcript", TestContext.Current.CancellationToken);
            await LiveTestWait.UntilAsync(() => transport.Sent.Count == 1);
            using var setup = JsonDocument.Parse(transport.Sent.Single());
            var config = setup.RootElement.GetProperty("setup");
            Assert.Equal("models/" + model, config.GetProperty("model").GetString());
            bool hasProactivity = config.TryGetProperty("proactivity", out var proactive);
            Assert.Equal(model.StartsWith("gemini-2.5-", StringComparison.Ordinal), hasProactivity);
            if (hasProactivity) Assert.True(proactive.GetProperty("proactiveAudio").GetBoolean());
            Assert.DoesNotContain("thinkingConfig", config.GetRawText());
            transport.Push("{\"setupComplete\":{}}");
            await LiveTestWait.UntilAsync(() => transport.Sent.Count == 2);
            Assert.Equal(model, session.SessionModel);
            await session.StopAsync();
            Assert.Equal(model, session.SessionModel);
        }
    }

    [Fact]
    public async Task UnsupportedLiveModelFailsBeforeConnectionWithoutFallback()
    {
        var transport = new FakeLiveTransport();
        await using var session = new GeminiLiveSession(transport, new SilentLivePlayback(), new FakeLiveMicrophone());
        await session.RunAsync("fake-key", Options() with { Model = "gemini-3.8-live-extended-thinking" }, "Text", TestContext.Current.CancellationToken);
        Assert.Equal(LiveSessionState.Failed, session.State);
        Assert.Equal(0, transport.ConnectCount);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task FragmentedSetupMultiplePartsAndMultipleTranscriptTurnsNeverOpenMicrophone()
    {
        var transport = new FakeLiveTransport();
        var player = new SilentLivePlayback();
        var mic = new FakeLiveMicrophone();
        await using var session = new GeminiLiveSession(transport, player, mic);
        Task run = session.RunAsync("fake-key", Options(), "Original transcript", TestContext.Current.CancellationToken);
        await LiveTestWait.UntilAsync(() => transport.Sent.Count == 1);
        Assert.Equal(0, mic.Subscriptions);
        transport.Push("{\"setupComplete\":{}}", fragmented: true);
        await LiveTestWait.UntilAsync(() => transport.Sent.Count == 2);
        Assert.Contains("Original transcript", transport.Sent.Last());
        Assert.False(session.CanSendText);
        transport.Push(AudioEvent(), fragmented: true);
        await LiveTestWait.UntilAsync(() => session.ResponseText.Contains("Hello"));
        Assert.Equal(2, player.Received.Count);
        Assert.DoesNotContain("duplicate", session.ResponseText);
        Assert.NotNull(session.ConnectionTime);
        Assert.NotNull(session.FirstAudioTime);
        Assert.False(session.CanSendText); // Server finished, but audible samples have not drained.
        player.Drain();
        await LiveTestWait.UntilAsync(() => session.CanSendText);
        await session.SendTextAsync("Second snapshot", TestContext.Current.CancellationToken);
        transport.Push(AudioEvent("Second turn"));
        await LiveTestWait.UntilAsync(() => session.ResponseText.Contains("Second turn"));
        Assert.Equal("Hello" + Environment.NewLine + "Second turn" + Environment.NewLine, session.ResponseText);
        Assert.Equal(0, mic.Subscriptions);
        Assert.DoesNotContain(transport.Sent, json => JsonDocument.Parse(json).RootElement.TryGetProperty("realtimeInput", out _));
        await session.StopAsync();
        await run;
        Assert.Equal(LiveSessionState.Closed, session.State);
        Assert.Equal(0, player.BufferedBytes);
    }

    [Fact]
    public async Task InterruptionDiscardsCancelledEventAudioAndNextTurnStillWorks()
    {
        var transport = new FakeLiveTransport();
        var player = new SilentLivePlayback();
        await using var session = new GeminiLiveSession(transport, player, new FakeLiveMicrophone());
        _ = session.RunAsync("fake-key", Options(), "Text", TestContext.Current.CancellationToken);
        transport.Push("{\"setupComplete\":{}}");
        await LiveTestWait.UntilAsync(() => transport.Sent.Count == 2);
        transport.Push(AudioEvent("Partial reply", false));
        await LiveTestWait.UntilAsync(() => player.Received.Count == 2);
        var interrupted = JsonNode.Parse(AudioEvent("must be discarded"))!;
        interrupted["serverContent"]!["interrupted"] = true;
        transport.Push(interrupted.ToJsonString());
        await LiveTestWait.UntilAsync(() => session.ResponseText.Contains("[interrupted]"));
        Assert.Equal(0, player.BufferedBytes);
        Assert.Equal(2, player.Received.Count);
        Assert.DoesNotContain("must be discarded", session.ResponseText);
        await session.SendTextAsync("Next turn", TestContext.Current.CancellationToken);
        transport.Push(AudioEvent("Recovered"));
        await LiveTestWait.UntilAsync(() => session.ResponseText.Contains("Recovered"));
        Assert.Equal(4, player.Received.Count);
        await session.StopAsync();
    }

    [Fact]
    public async Task MicrophoneStreamsOnlyAfterSetupAndStopsUploadingOnStop()
    {
        var transport = new FakeLiveTransport();
        var player = new SilentLivePlayback();
        var mic = new FakeLiveMicrophone();
        await using var session = new GeminiLiveSession(transport, player, mic);
        _ = session.RunAsync("fake-key", Options(true), "Never upload this saved text", TestContext.Current.CancellationToken);
        await LiveTestWait.UntilAsync(() => transport.Sent.Count == 1);
        Assert.Equal(0, mic.Subscriptions);
        transport.Push("{\"setupComplete\":{}}");
        await LiveTestWait.UntilAsync(() => mic.Subscriptions == 1);
        mic.Queue.TryWrite(new byte[] { 1, 2, 3, 4 });
        await LiveTestWait.UntilAsync(() => transport.Sent.Count == 2);
        Assert.Contains("audio/pcm;rate=16000", transport.Sent.Last());
        Assert.DoesNotContain(transport.Sent, json => json.Contains("clientContent"));
        transport.Push(AudioEvent());
        await LiveTestWait.UntilAsync(() => player.Received.Count == 2);
        Assert.Null(session.FirstAudioTime);
        await session.StopAsync();
        Assert.Equal(1, mic.Releases);
        int sent = transport.Sent.Count;
        int played = player.Received.Count;
        Assert.False(mic.Queue.TryWrite(new byte[] { 1, 2 }));
        transport.Push(AudioEvent("Too late"));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(sent, transport.Sent.Count);
        Assert.Equal(played, player.Received.Count);
    }

    [Fact]
    public async Task StopCancelsBlockedMicrophoneSendWithoutUploadingQueuedAudio()
    {
        var transport = new FakeLiveTransport();
        var microphone = new FakeLiveMicrophone();
        await using var session = new GeminiLiveSession(transport, new SilentLivePlayback(), microphone);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Sending = async (json, token) =>
        {
            if (!json.Contains("realtimeInput\"")) return;
            blocked.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        _ = session.RunAsync("fake-key", Options(true), "", TestContext.Current.CancellationToken);
        transport.Push("{\"setupComplete\":{}}");
        await LiveTestWait.UntilAsync(() => microphone.Subscriptions == 1);
        microphone.Queue.TryWrite(new byte[] { 1, 2 });
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        microphone.Queue.TryWrite(new byte[] { 3, 4 });
        await session.StopAsync();
        Assert.Single(transport.Sent); // Only setup was committed to the transport.
        Assert.Equal(1, microphone.Releases);
        Assert.False(session.IsActive);
    }

    [Fact]
    public async Task DeadlineIncludesBlockedConnectionAndAbortsWithoutReconnect()
    {
        var transport = new FakeLiveTransport { Connecting = token => Task.Delay(Timeout.Infinite, token) };
        var player = new SilentLivePlayback();
        await using var session = new GeminiLiveSession(transport, player, new FakeLiveMicrophone(), TimeSpan.FromMilliseconds(80));
        await session.RunAsync("fake-key", Options(true), "", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(LiveSessionState.TimedOut, session.State);
        Assert.True(transport.Aborted);
        Assert.Equal(1, transport.ConnectCount);
        Assert.Empty(transport.Sent);
        Assert.False(session.IsActive);
    }

    [Fact]
    public async Task DeadlineStopsMicrophonePlaybackAndMarksIncompleteText()
    {
        var transport = new FakeLiveTransport();
        var player = new SilentLivePlayback();
        var mic = new FakeLiveMicrophone();
        await using var session = new GeminiLiveSession(transport, player, mic, TimeSpan.FromMilliseconds(300));
        Task run = session.RunAsync("fake-key", Options(true), "", TestContext.Current.CancellationToken);
        transport.Push("{\"setupComplete\":{}}");
        await LiveTestWait.UntilAsync(() => mic.Subscriptions == 1);
        transport.Push(AudioEvent("In progress", false));
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(LiveSessionState.TimedOut, session.State);
        Assert.Contains("[timed out / incomplete]", session.ResponseText);
        Assert.Equal(0, player.BufferedBytes);
        Assert.Equal(1, mic.Releases);
        Assert.True(transport.Aborted);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"error\":{\"code\":401,\"message\":\"fake-secret private transcript\"}}")]
    [InlineData("{\"error\":{\"code\":429,\"message\":\"fake-secret\"}}")]
    [InlineData("{\"goAway\":{\"timeLeft\":\"5s\"}}")]
    [InlineData("{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/mp3\",\"data\":\"AQI=\"}}]}}}")]
    [InlineData("{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/pcm;rate=24000\",\"data\":\"AQ==\"}}]}}}")]
    public async Task InvalidMessagesAndServerErrorsStopWithSanitizedErrors(string message)
    {
        var transport = new FakeLiveTransport();
        var player = new SilentLivePlayback();
        await using var session = new GeminiLiveSession(transport, player, new FakeLiveMicrophone());
        string? error = null;
        session.Error += text => error = text;
        Task run = session.RunAsync("fake-secret", Options(), "Text", TestContext.Current.CancellationToken);
        transport.Push("{\"setupComplete\":{}}");
        transport.Push(message);
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(LiveSessionState.Failed, session.State);
        Assert.NotNull(error);
        Assert.DoesNotContain("fake-secret", error);
        Assert.DoesNotContain("private transcript", error);
        Assert.Equal(0, player.BufferedBytes);
    }

    [Fact]
    public async Task UnexpectedDisconnectAndTransportExceptionsNeverExposeCloseReasonOrKey()
    {
        foreach (bool connectFailure in new[] { false, true })
        {
            var transport = new FakeLiveTransport();
            if (connectFailure) transport.Connecting = _ => throw new Exception("wss://secret-url?key=fake-secret");
            else transport.Disconnect();
            await using var session = new GeminiLiveSession(transport, new SilentLivePlayback(), new FakeLiveMicrophone());
            string? error = null;
            session.Error += value => error = value;
            await session.RunAsync("fake-secret", Options(), "Text", TestContext.Current.CancellationToken);
            Assert.Equal(LiveSessionState.Failed, session.State);
            Assert.NotNull(error);
            Assert.DoesNotContain("fake-secret", error);
            Assert.DoesNotContain("secret-url", error);
        }
    }

    [Fact]
    public async Task OutputOverflowAndMicrophoneFailureStopInsteadOfDroppingAudio()
    {
        foreach (bool inputFailure in new[] { false, true })
        {
            var transport = new FakeLiveTransport();
            var player = new SilentLivePlayback();
            var mic = new FakeLiveMicrophone();
            await using var session = new GeminiLiveSession(transport, player, mic);
            string? error = null;
            session.Error += value => error = value;
            Task run = session.RunAsync("fake-key", Options(true), "", TestContext.Current.CancellationToken);
            transport.Push("{\"setupComplete\":{}}");
            await LiveTestWait.UntilAsync(() => mic.Subscriptions == 1);
            if (inputFailure) mic.Queue.Complete(new InvalidOperationException("fake-private-error"));
            else
            {
                player.Append(new byte[LiveAudioPlayback.MaximumBufferedBytes]);
                transport.Push(AudioEvent());
            }
            await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(LiveSessionState.Failed, session.State);
            Assert.Contains("buffer", error);
            Assert.DoesNotContain("fake-private-error", error);
            Assert.Equal(0, player.BufferedBytes);
            Assert.Equal(1, mic.Releases);
        }
    }

    [Fact]
    public async Task ManualStopBoundsGracefulCloseAndFreshStartRequiresNewSession()
    {
        var transport = new FakeLiveTransport { Closing = _ => new TaskCompletionSource().Task };
        await using var session = new GeminiLiveSession(transport, new SilentLivePlayback(), new FakeLiveMicrophone());
        _ = session.RunAsync("fake-key", Options(), "Text", TestContext.Current.CancellationToken);
        await LiveTestWait.UntilAsync(() => transport.Sent.Count == 1);
        var timer = Stopwatch.StartNew();
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(transport.Aborted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync("fake-key", Options(), "Text", TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "NativeLive")]
    public async Task OptInNativeMicrophoneTrialWithLocalCaptionsAndAutomaticExpiry()
    {
        if (Environment.GetEnvironmentVariable("CONTENT_COHOST_RUN_NATIVE_LIVE_TEST") != "1")
            Assert.Skip("Opt-in native microphone/headphone trial: set CONTENT_COHOST_RUN_NATIVE_LIVE_TEST=1 and GEMINI_API_KEY, then follow README's spoken trial.");
        string? key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) Assert.Skip("GEMINI_API_KEY is required.");
        var microphone = new MicrophoneCaptureService();
        var captions = new TranscriptionService(microphone: microphone);
        string? captionError = null;
        captions.Error += value => captionError = value;
        captions.Start(0);
        try
        {
            await using var session = new GeminiLiveSession(new LiveWebSocketTransport(), new LiveAudioPlayback(), microphone);
            string? liveError = null;
            session.Error += value => liveError = value;
            await session.RunAsync(key, Options(true), "", TestContext.Current.CancellationToken);
            Assert.Equal(LiveSessionState.TimedOut, session.State);
            Assert.Null(liveError);
            Assert.Null(captionError);
            Assert.True(captions.IsRunning); // Live released only its microphone subscription.
        }
        finally { await captions.StopAsync(); }
    }

    [Fact]
    [Trait("Category", "LiveApi")]
    public async Task OptInRealLiveTranscriptTwoTurnsUsesSilentPlayback()
    {
        if (Environment.GetEnvironmentVariable("CONTENT_COHOST_RUN_LIVE_API_TEST") != "1")
            Assert.Skip("Set CONTENT_COHOST_RUN_LIVE_API_TEST=1 and GEMINI_API_KEY for this paid Live WebSocket check.");
        string? key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) Assert.Skip("GEMINI_API_KEY is required.");
        var player = new SilentLivePlayback { DrainImmediately = true };
        var mic = new FakeLiveMicrophone();
        await using var session = new GeminiLiveSession(new LiveWebSocketTransport(), player, mic);
        string? error = null;
        session.Error += value => error = value;
        string model = Environment.GetEnvironmentVariable("CONTENT_COHOST_LIVE_API_MODEL") ?? GeminiLiveSession.Model;
        _ = session.RunAsync(key, Options() with { Model = model }, "Co-host, say one short sentence about a robot dropping its script.", TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(29));
        for (int turn = 0; turn < 2; turn++)
        {
            while (!session.CanSendText && session.IsActive) await Task.Delay(20, timeout.Token);
            Assert.True(session.CanSendText, error ?? "Live ended before completing the transcript turn.");
            if (turn == 0) await session.SendTextAsync("Co-host, give one more brief friendly joke.", timeout.Token);
        }
        Assert.NotEmpty(player.Received);
        Assert.NotEmpty(session.ResponseText);
        Assert.NotNull(session.ConnectionTime);
        Assert.NotNull(session.FirstAudioTime);
        Assert.Equal(0, mic.Subscriptions);
        await session.StopAsync();
    }
}
