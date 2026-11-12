using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class SpeechTests
{
    [Fact]
    public async Task SpeechRequestSeparatesVerbatimTextStyleAndVoiceWithoutStoringInteraction()
    {
        using var handler = new SpeechHttpHandler(async (request, token) =>
        {
            Assert.Equal(GeminiSpeechService.Endpoint, request.RequestUri);
            Assert.Equal("fake-secret", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            string body = await request.Content!.ReadAsStringAsync(token);
            Assert.DoesNotContain("fake-secret", body);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal(GeminiSpeechService.Model, root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            var text = root.GetProperty("input")[0].GetProperty("content")[0];
            Assert.Equal("Hello creator.", text.GetProperty("text").GetString());
            Assert.Equal("Dry deadpan.", text.GetProperty("annotations")[0].GetProperty("style").GetString());
            Assert.Equal("Kore", root.GetProperty("generation_config").GetProperty("speech_config")[0].GetProperty("voice").GetString());
            return SpeechTestAudio.Response();
        });
        using var client = new HttpClient(handler);
        byte[] wav = await new GeminiSpeechService(client, "fake-secret").SynthesizeAsync("Hello creator.", "Kore", "Dry deadpan.", TestContext.Current.CancellationToken);
        GeminiSpeechService.ValidateWav(wav);
    }

    [Theory]
    [InlineData(400, "rejected")]
    [InlineData(401, "authentication")]
    [InlineData(403, "authentication")]
    [InlineData(404, "model")]
    [InlineData(429, "quota")]
    [InlineData(503, "high demand")]
    public async Task SpeechErrorsAreReadableAndDoNotExposeRemoteBodies(int status, string expected)
    {
        int requests = 0;
        using var handler = new SpeechHttpHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("fake-secret echoed by server") });
        });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GeminiSpeechService(client, "fake-secret")
            .SynthesizeAsync("Hello", "Kore", "deadpan", TestContext.Current.CancellationToken));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("fake-secret", error.ToString());
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"steps\":null}")]
    [InlineData("{\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"audio\",\"data\":\"not base64\"}]}]}")]
    [InlineData("{\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"text\",\"text\":\"blocked\"}]}]}")]
    [InlineData("{\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"audio\",\"mime_type\":\"audio/l16\",\"data\":\"AAAA\"}]}]}")]
    public void MissingBlockedOrMalformedAudioProducesSafeError(string json)
    {
        using var document = JsonDocument.Parse(json);
        var error = Assert.Throws<InvalidOperationException>(() => GeminiSpeechService.ReadAudio(document.RootElement));
        Assert.Contains("audio", error.Message);
    }

    [Fact]
    public void AudioParserUsesTheLastModelAudioBlock()
    {
        using var document = JsonDocument.Parse(new JsonObject
        {
            ["steps"] = new JsonArray(new JsonObject
            {
                ["type"] = "model_output",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "audio", ["data"] = "not base64" },
                    new JsonObject { ["type"] = "audio", ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(SpeechTestAudio.CreateWav()) })
            })
        }.ToJsonString());
        GeminiSpeechService.ValidateWav(GeminiSpeechService.ReadAudio(document.RootElement));
    }

    [Fact]
    public void InvalidAndEmptyWavAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => GeminiSpeechService.ValidateWav(new byte[60]));
        Assert.Throws<InvalidOperationException>(() => GeminiSpeechService.ValidateWav(SpeechTestAudio.CreateWav(0)));
        Assert.Throws<InvalidOperationException>(() => GeminiSpeechService.ValidateWav(SpeechTestAudio.CreateWav(20, 16000)));
        byte[] truncated = SpeechTestAudio.CreateWav()[..30];
        Assert.Throws<InvalidOperationException>(() => GeminiSpeechService.ValidateWav(truncated));
        byte[] truncatedData = SpeechTestAudio.CreateWav()[..60];
        Assert.Throws<InvalidOperationException>(() => GeminiSpeechService.ValidateWav(truncatedData));
    }

    [Fact]
    public async Task NetworkErrorsAndUnreadableJsonAreSanitized()
    {
        using var handler = new SpeechHttpHandler((_, _) => throw new HttpRequestException("fake-secret"));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GeminiSpeechService(client, "fake-secret")
            .SynthesizeAsync("Hello", "Kore", "deadpan", TestContext.Current.CancellationToken));
        Assert.DoesNotContain("fake-secret", error.ToString());
        using var invalidHandler = new SpeechHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("fake-secret invalid JSON")
        }));
        using var invalidClient = new HttpClient(invalidHandler);
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() => new GeminiSpeechService(invalidClient, "fake-secret")
            .SynthesizeAsync("Hello", "Kore", "deadpan", TestContext.Current.CancellationToken));
        Assert.Contains("unreadable", invalid.Message);
        Assert.DoesNotContain("fake-secret", invalid.ToString());
    }

    [Fact]
    public async Task CancellationPropagatesAndClientTimeoutIsReadable()
    {
        using var handler = new SpeechHttpHandler((_, _) => throw new TaskCanceledException());
        using var client = new HttpClient(handler);
        var service = new GeminiSpeechService(client, "fake-secret");
        var timeout = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynthesizeAsync("Hello", "Kore", "deadpan", CancellationToken.None));
        Assert.Contains("too long", timeout.Message);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SynthesizeAsync("Hello", "Kore", "deadpan", source.Token));
    }

    [Theory]
    [InlineData("", "Hello", "Kore")]
    [InlineData("bad\nkey", "Hello", "Kore")]
    [InlineData("key", " ", "Kore")]
    [InlineData("key", "Hello", "Unknown")]
    public async Task InvalidSettingsDoNotSendRequests(string key, string text, string voice)
    {
        using var handler = new SpeechHttpHandler((_, _) => throw new Xunit.Sdk.XunitException("No request expected."));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GeminiSpeechService(client, key)
            .SynthesizeAsync(text, voice, "deadpan", TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "LiveSpeech")]
    public async Task LiveSpeechGeneratesWavForBothPresetsWithoutPlayingSound()
    {
        if (Environment.GetEnvironmentVariable("CONTENT_COHOST_RUN_LIVE_TTS_TEST") != "1")
            Assert.Skip("Set CONTENT_COHOST_RUN_LIVE_TTS_TEST=1 and GEMINI_API_KEY for the paid speech check.");
        string? key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            Assert.Skip("GEMINI_API_KEY is required for this check.");
        using var client = GeminiService.CreateClient();
        var speech = new GeminiSpeechService(client, key);
        foreach (string style in new[] { GeminiSpeechService.RobotStyle, GeminiSpeechService.DocumentaryStyle })
        {
            byte[] wav = await speech.SynthesizeAsync("Well, that went exactly as planned. Mostly.", "Kore", style, TestContext.Current.CancellationToken);
            GeminiSpeechService.ValidateWav(wav);
        }
    }
}

internal sealed class SpeechHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}

internal static class SpeechTestAudio
{
    public static byte[] CreateWav(int milliseconds = 20, int sampleRate = 24000)
    {
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(sampleRate, 16, 1)))
            writer.Write(new byte[sampleRate * 2 * milliseconds / 1000], 0, sampleRate * 2 * milliseconds / 1000);
        return stream.ToArray();
    }

    public static HttpResponseMessage Response() => new(HttpStatusCode.OK)
    {
        Content = JsonContent(new JsonObject
        {
            ["steps"] = new JsonArray(new JsonObject
            {
                ["type"] = "model_output",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "audio",
                    ["mime_type"] = "audio/wav",
                    ["data"] = Convert.ToBase64String(CreateWav())
                })
            })
        })
    };

    private static StringContent JsonContent(JsonObject json) => new(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
}
