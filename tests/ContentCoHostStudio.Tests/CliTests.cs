using System.Net;
using System.Text.Json;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class CliTests
{
    [Fact]
    public async Task NarratePreservesExactUnicodeScriptAndSeparatesStyleWithoutTextGeneration()
    {
        var exporter = new FakeNarrationExporter();
        int calls = 0;
        using var handler = new SpeechHttpHandler(async (message, token) =>
        {
            Assert.True(exporter.Prepared);
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            var input = json.RootElement.GetProperty("input")[0].GetProperty("content")[0];
            Assert.Equal("Très bien — robot 🤖.", input.GetProperty("text").GetString());
            Assert.Equal("deadpan", input.GetProperty("annotations")[0].GetProperty("style").GetString());
            Assert.Equal("gemini-3.8-flash-lite-tts", json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.GetProperty("store").GetBoolean());
            calls++;
            return SpeechTestAudio.Response();
        });
        using var client = new HttpClient(handler);
        using var output = new StringWriter(); using var error = new StringWriter();
        int code = await new CliApplication(client, "fake-key", exporter: exporter).RunAsync(["narrate", "--request", "-"],
            new StringReader("""{"text":"Très bien — robot 🤖.","output":"clip.webm","voiceStyle":"deadpan","speechModel":"gemini-3.8-flash-lite-tts"}"""), output, error, TestContext.Current.CancellationToken);
        Assert.Equal(0, code); Assert.Equal(1, calls);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.False(result.RootElement.GetProperty("result").TryGetProperty("spokenText", out _));
        Assert.DoesNotContain("Très", error.ToString()); Assert.DoesNotContain("fake-key", output.ToString() + error);
        Assert.NotNull(exporter.Scene); Assert.Equal(1, exporter.Exports);
    }

    [Fact]
    public async Task ReplyUsesSeparatePersonalityAndContextAndReturnsTextOnlyWhenRequested()
    {
        int calls = 0;
        var exporter = new FakeNarrationExporter();
        using var handler = new SpeechHttpHandler(async (message, token) =>
        {
            calls++;
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            if (calls == 1)
            {
                Assert.Contains("gemini-3.5-flash-lite", message.RequestUri!.AbsolutePath);
                Assert.Equal("friendly robot", body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
                Assert.Equal("context only", body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
                return ModelTests.Json("""{"candidates":[{"content":{"parts":[{"text":"That went almost to plan."}]}}]}""");
            }
            Assert.Equal("That went almost to plan.", body.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
            return SpeechTestAudio.Response();
        });
        using var client = new HttpClient(handler);
        using var output = new StringWriter(); using var error = new StringWriter();
        int code = await new CliApplication(client, "fake-key", exporter: exporter).RunAsync(["reply", "--request", "-"],
            new StringReader("""{"context":"context only","personality":"friendly robot","output":"clip.webm","returnText":true,"textModel":"gemini-3.5-flash-lite"}"""), output, error, TestContext.Current.CancellationToken);
        Assert.Equal(0, code); Assert.Equal(2, calls);
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("That went almost to plan.", result.GetProperty("spokenText").GetString());
        Assert.Equal("gemini-3.5-flash-lite", result.GetProperty("textModel").GetString());
        Assert.DoesNotContain("context only", error.ToString());
    }

    [Theory]
    [InlineData("{bad JSON}")]
    [InlineData("{\"text\":\"hi\",\"output\":\"a.webm\",\"apiKey\":\"private-key\"}")]
    [InlineData("{\"text\":\"hi\",\"output\":\"a.mp4\"}")]
    [InlineData("{\"text\":\"hi\",\"context\":\"mixed\",\"output\":\"a.webm\"}")]
    [InlineData("{\"text\":\"hi\",\"output\":\"a.webm\",\"voice\":\"Unknown\"}")]
    [InlineData("{\"text\":\"hi\",\"output\":\"a.webm\",\"speechModel\":\"gemini-9-tts\"}")]
    [InlineData("{\"text\":\"hi\",\"output\":\"a.webm\",\"size\":800}")]
    [InlineData("{\"text\":\"hi\",\"output\":\"a.webm\",\"shake\":101}")]
    public async Task InvalidJobsFailBeforePaidRequests(string request)
    {
        using var handler = new SpeechHttpHandler((_, _) => throw new Xunit.Sdk.XunitException("No network expected."));
        using var client = new HttpClient(handler); using var output = new StringWriter(); using var error = new StringWriter();
        var exporter = new FakeNarrationExporter();
        int code = await new CliApplication(client, "fake-key", exporter: exporter).RunAsync(["narrate", "--request", "-"],
            new StringReader(request), output, error, TestContext.Current.CancellationToken);
        Assert.Equal(2, code); Assert.False(exporter.Prepared); Assert.DoesNotContain("private-key", output.ToString());
    }

    [Fact]
    public async Task DependencyFailureMissingAvatarAndMissingKeyNeverGenerateSpeech()
    {
        using var handler = new SpeechHttpHandler((_, _) => throw new Xunit.Sdk.XunitException("No network expected."));
        using var client = new HttpClient(handler);
        foreach (var (request, key, exporter, expected) in new[]
        {
            ("{\"text\":\"hi\",\"output\":\"a.webm\"}", "", new FakeNarrationExporter(), 2),
            ("{\"text\":\"hi\",\"output\":\"a.webm\",\"avatarId\":\"missing\"}", "key", new FakeNarrationExporter(), 2),
            ("{\"text\":\"hi\",\"output\":\"a.webm\"}", "key", new FakeNarrationExporter { PrepareFailure = new CliException(CliExitCode.Dependency, "Encoder unavailable.") }, 3)
        })
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            int code = await new CliApplication(client, key, exporter: exporter).RunAsync(["narrate", "--request", "-"], new StringReader(request), output, error, TestContext.Current.CancellationToken);
            Assert.Equal(expected, code); Assert.Equal(0, exporter.Exports);
        }
    }

    [Fact]
    public async Task GenerationErrorsAreSanitizedAndCancellationHasStableExitCode()
    {
        using var handler = new SpeechHttpHandler((_, _) => throw new Exception("private-key transcript echo"));
        using var client = new HttpClient(handler); using var output = new StringWriter(); using var error = new StringWriter();
        var exporter = new FakeNarrationExporter();
        int code = await new CliApplication(client, "private-key", exporter: exporter).RunAsync(["narrate", "--request", "-"],
            new StringReader("{\"text\":\"sensitive transcript\",\"output\":\"a.webm\"}"), output, error, TestContext.Current.CancellationToken);
        Assert.Equal(4, code); Assert.DoesNotContain("private-key", output.ToString() + error); Assert.DoesNotContain("transcript", output.ToString() + error);
        Assert.Equal(0, exporter.Exports);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var cancelledOutput = new StringWriter();
        Assert.Equal(130, await new CliApplication(client, "key").RunAsync(["avatars", "list"], new StringReader(""), cancelledOutput, error, cancelled.Token));
    }

    [Fact]
    public async Task CancellationInterruptsAStalledInputReaderAndAnInFlightSpeechRequest()
    {
        using var handler = new SpeechHttpHandler((_, _) => throw new Xunit.Sdk.XunitException("No HTTP during input parsing."));
        using var client = new HttpClient(handler); using var output = new StringWriter(); using var error = new StringWriter();
        using var stop = new CancellationTokenSource();
        using var stalled = new StalledReader();
        Task<int> reading = new CliApplication(client, "key").RunAsync(["narrate", "--request", "-"], stalled, output, error, stop.Token);
        await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        stop.Cancel();
        Assert.Equal(130, await reading.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        stalled.Release.Set();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var speechHandler = new SpeechHttpHandler(async (_, token) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return SpeechTestAudio.Response();
        });
        using var speechClient = new HttpClient(speechHandler); using var secondStop = new CancellationTokenSource();
        var exporter = new FakeNarrationExporter(); using var secondOutput = new StringWriter();
        Task<int> generating = new CliApplication(speechClient, "key", exporter: exporter).RunAsync(["narrate", "--request", "-"],
            new StringReader("{\"text\":\"Hello\",\"output\":\"a.webm\"}"), secondOutput, error, secondStop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        secondStop.Cancel();
        Assert.Equal(130, await generating);
        Assert.Equal(0, exporter.Exports);
    }

    private sealed class StalledReader : TextReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); Release.Wait(); return ValueTask.FromResult(0);
        }
        protected override void Dispose(bool disposing) { Release.Set(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task ModelsListUsesOnlyCatalogAndHelpAndAvatarsNeedNoKey()
    {
        using var handler = new SpeechHttpHandler((message, _) =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            return Task.FromResult(ModelTests.Json("""{"models":[{"name":"models/gemini-3.6-flash","supportedGenerationMethods":["generateContent"]}]}"""));
        });
        using var client = new HttpClient(handler);
        foreach (var command in new[] { new[] { "--help" }, new[] { "avatars", "list" }, new[] { "models", "list" } })
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(0, await new CliApplication(client, command[0] == "models" ? "key" : null).RunAsync(command, new StringReader(""), output, error, TestContext.Current.CancellationToken));
            using var result = JsonDocument.Parse(output.ToString()); Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        }
    }
}

internal sealed class FakeNarrationExporter : INarrationExporter
{
    public bool Prepared;
    public int Exports;
    public VideoExportScene? Scene;
    public Exception? PrepareFailure;
    public Task<VideoExportOptions> PrepareAsync(NarrationJobRequest request, CancellationToken token)
    {
        if (PrepareFailure != null) throw PrepareFailure;
        Prepared = true;
        return Task.FromResult(new VideoExportOptions(Path.GetFullPath(request.Output), request.Size, request.Overwrite, new("fake", "fake")));
    }
    public Task<VideoExportResult> ExportAsync(AvatarPack pack, VideoExportScene scene, VideoExportOptions options, IProgress<JobProgress>? progress, CancellationToken token)
    { Exports++; Scene = scene; return Task.FromResult(new VideoExportResult(options.Destination, (int)Math.Round(scene.DurationMs))); }
}
