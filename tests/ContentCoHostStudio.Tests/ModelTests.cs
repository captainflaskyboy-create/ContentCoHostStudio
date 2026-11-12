using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class ModelTests
{
    [Fact]
    public async Task CatalogLoadsEveryPageDeduplicatesAndAuthenticatesWithoutKeyInUrl()
    {
        int calls = 0;
        using var handler = new SpeechHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("fake-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            Assert.DoesNotContain("fake-key", request.RequestUri!.ToString());
            calls++;
            if (calls == 1)
                return Task.FromResult(Json("""{"models":[{"name":"models/gemini-3.6-flash","supportedGenerationMethods":["generateContent"]}],"nextPageToken":"next /+"}"""));
            Assert.Contains("pageToken=next%20%2F%2B", request.RequestUri.AbsoluteUri);
            return Task.FromResult(Json("""{"models":[{"name":"models/gemini-3.6-flash","supportedGenerationMethods":["generateContent"]},{"name":"models/gemini-3.8-flash-lite-tts","supportedGenerationMethods":["generateContent"]}]}"""));
        });
        using var client = new HttpClient(handler);
        var models = await new GeminiModelCatalog(client).LoadAsync("fake-key", TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Equal(2, models.Count);
        Assert.Contains(models, model => model.Kind == GeminiModelKind.Speech && model.IsSupported);
    }

    [Theory]
    [InlineData("gemini-embedding-001")]
    [InlineData("gemini-3-pro-image")]
    [InlineData("gemini-3.5-transcribe")]
    [InlineData("gemini-robotics-er-1.6-preview")]
    [InlineData("gemini-3-computer-use-preview")]
    [InlineData("gemini-omni-flash-preview")]
    [InlineData("gemma-4")]
    [InlineData("gemini-x/../../other")]
    public void SpecializedAndInvalidModelsAreExcluded(string id) => Assert.Null(GeminiModelCatalog.Classify(id, ["generateContent"]));

    [Theory]
    [InlineData("gemini-3.8-live", true)]
    [InlineData("gemini-3.1-flash-live-preview", true)]
    [InlineData("gemini-2.5-flash-native-audio-preview-12-2025", true)]
    [InlineData("gemini-3.8-live-extended-thinking", false)]
    [InlineData("gemini-9-live", false)]
    [InlineData("gemini-3.5-live-translate-preview", false)]
    public void LiveModelsHaveIndependentCompatibilityAndRequireBidirectionalSupport(string id, bool supported)
    {
        var model = GeminiModelCatalog.Classify(id, ["bidiGenerateContent"]);
        Assert.NotNull(model);
        Assert.Equal(GeminiModelKind.Live, model.Kind);
        Assert.Equal(supported, model.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(model.Explanation));
        Assert.False(GeminiModelCatalog.Classify(id, ["generateContent"])!.IsSupported);
    }

    [Theory]
    [InlineData("gemini-3.8-flash-tts", SpeechProtocol.InteractionsWav)]
    [InlineData("gemini-3.8-flash-lite-tts", SpeechProtocol.InteractionsWav)]
    [InlineData("gemini-3.1-flash-tts-preview", SpeechProtocol.LegacyPcm)]
    [InlineData("gemini-2.5-flash-preview-tts", SpeechProtocol.LegacyPcm)]
    [InlineData("gemini-2.5-pro-preview-tts", SpeechProtocol.LegacyPcm)]
    [InlineData("gemini-9-flash-tts", SpeechProtocol.Unsupported)]
    public void SpeechProtocolsAreExplicitAndUnknownOnesAreDisabled(string id, SpeechProtocol expected)
    {
        Assert.Equal(expected, GeminiModelCatalog.GetSpeechProtocol(id));
        var option = GeminiModelCatalog.Classify(id, ["generateContent"]);
        Assert.NotNull(option);
        Assert.Equal(GeminiModelKind.Speech, option.Kind);
        Assert.Equal(expected != SpeechProtocol.Unsupported, option.IsSupported);
        if (!option.IsSupported)
            Assert.False(string.IsNullOrWhiteSpace(option.Explanation));
    }

    [Fact]
    public void PreviewMissingAndUnsupportedLabelsAreVisible()
    {
        var option = new GeminiModelOption("gemini-3-flash-preview", GeminiModelKind.Text, false, "Unsupported", true);
        Assert.Contains("preview/experimental", option.Label);
        Assert.Contains("not in latest catalog", option.Label);
        Assert.Contains("unsupported", option.Label);
        Assert.False(GeminiModelCatalog.Classify("gemini-3.6-flash", [])!.IsSupported);
    }

    [Theory]
    [InlineData("{\"models\":[]}")]
    [InlineData("{}")]
    public async Task EmptyCatalogIsAValidResult(string body)
    {
        using var handler = new SpeechHttpHandler((_, _) => Task.FromResult(Json(body)));
        using var client = new HttpClient(handler);
        Assert.Empty(await new GeminiModelCatalog(client).LoadAsync("key", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{\"models\":null}")]
    [InlineData("{\"models\":[{}]}")]
    [InlineData("not-json-secret")]
    [InlineData("{\"models\":[],\"nextPageToken\":\"repeated\"}")]
    public async Task MalformedCatalogAndRepeatedPageTokensFailSafely(string body)
    {
        using var handler = new SpeechHttpHandler((_, _) => Task.FromResult(Json(body)));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GeminiModelCatalog(client).LoadAsync("key", TestContext.Current.CancellationToken));
        Assert.Contains("unreadable", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task CatalogAuthenticationAndNetworkErrorsAreSanitized()
    {
        using var handler = new SpeechHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("secret echoed by server")
        }));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GeminiModelCatalog(client).LoadAsync("key", TestContext.Current.CancellationToken));
        Assert.Contains("authentication", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
        using var networkHandler = new SpeechHttpHandler((_, _) => throw new HttpRequestException("secret"));
        using var networkClient = new HttpClient(networkHandler);
        var network = await Assert.ThrowsAsync<InvalidOperationException>(() => new GeminiModelCatalog(networkClient).LoadAsync("key", TestContext.Current.CancellationToken));
        Assert.DoesNotContain("secret", network.ToString());
    }

    [Fact]
    public async Task TextRequestUsesTheSelectedModel()
    {
        using var handler = new SpeechHttpHandler((request, _) =>
        {
            Assert.EndsWith("/gemini-3.5-flash-lite:generateContent", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"candidates":[{"content":{"parts":[{"text":"reply"}]}}]}"""));
        });
        using var client = new HttpClient(handler);
        Assert.Equal("reply", await new GeminiService(client).GenerateAsync("key", "personality", "hello", TestContext.Current.CancellationToken, "gemini-3.5-flash-lite"));
        Assert.Throws<InvalidOperationException>(() => GeminiService.EndpointFor("gemini-3.8-flash-tts"));
    }

    [Theory]
    [InlineData("gemini-3.1-flash-tts-preview")]
    [InlineData("gemini-2.5-flash-preview-tts")]
    [InlineData("gemini-2.5-pro-preview-tts")]
    public async Task LegacySpeechUsesItsOwnEndpointAndWrapsPcmInMemory(string model)
    {
        using var handler = new SpeechHttpHandler(async (request, token) =>
        {
            Assert.EndsWith($"/{model}:generateContent", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            string prompt = root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
            Assert.Contains("deadpan", prompt);
            Assert.Contains("Hello creator.", prompt);
            Assert.Equal("Puck", root.GetProperty("generationConfig").GetProperty("speechConfig").GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
            return LegacyResponse(new byte[2400], "audio/L16;codec=pcm;rate=24000");
        });
        using var client = new HttpClient(handler);
        byte[] wav = await new GeminiSpeechService(client, "key").SynthesizeAsync("Hello creator.", "Puck", "deadpan", TestContext.Current.CancellationToken, model);
        GeminiSpeechService.ValidateWav(wav);
        Assert.True(wav.Length > 2400);
    }

    [Theory]
    [InlineData("audio/mp3", 100)]
    [InlineData("audio/L16;rate=16000", 100)]
    [InlineData("audio/L16;channels=2;rate=24000", 100)]
    [InlineData("audio/L16;codec=other;rate=24000", 100)]
    [InlineData("audio/L16;rate=24000", 1)]
    [InlineData("audio/L16;rate=24000", 0)]
    public async Task UnsupportedLegacyAudioIsRejected(string mime, int count)
    {
        using var handler = new SpeechHttpHandler((_, _) => Task.FromResult(LegacyResponse(new byte[count], mime)));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GeminiSpeechService(client, "key")
            .SynthesizeAsync("Hello", "Kore", "deadpan", TestContext.Current.CancellationToken, "gemini-2.5-flash-preview-tts"));
        Assert.Contains("unsupported audio", error.Message);
    }

    [Fact]
    public async Task ModernSpeechCarriesTheSelectedModelAndLegacyAlsoAcceptsWav()
    {
        using var handler = new SpeechHttpHandler(async (request, token) =>
        {
            if (request.RequestUri == GeminiSpeechService.Endpoint)
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("gemini-3.8-flash-lite-tts", document.RootElement.GetProperty("model").GetString());
                return SpeechTestAudio.Response();
            }
            return LegacyResponse(SpeechTestAudio.CreateWav(), "audio/wav");
        });
        using var client = new HttpClient(handler);
        var service = new GeminiSpeechService(client, "key");
        GeminiSpeechService.ValidateWav(await service.SynthesizeAsync("Hello", "Kore", "deadpan", TestContext.Current.CancellationToken, "gemini-3.8-flash-lite-tts"));
        GeminiSpeechService.ValidateWav(await service.SynthesizeAsync("Hello", "Kore", "deadpan", TestContext.Current.CancellationToken, "gemini-3.1-flash-tts-preview"));
    }

    [Fact]
    public void TimingsFreezeOnFinishAndCachedReplayHasNoRequestDuration()
    {
        var timing = new OperationTiming();
        timing.Start();
        Assert.Contains("running", timing.Describe("Text"));
        timing.Finish("Failed");
        TimeSpan elapsed = timing.Elapsed;
        timing.Finish("Completed");
        Assert.Equal("Failed", timing.Outcome);
        Assert.Equal(elapsed, timing.Elapsed);
        timing.Start();
        timing.Finish("Cancelled");
        Assert.Contains("cancelled", timing.Describe("Voice"));
        timing.Cached();
        Assert.Equal(TimeSpan.Zero, timing.Elapsed);
        Assert.Contains("no API request", timing.Describe("Voice"));
    }

    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    internal static HttpResponseMessage LegacyResponse(byte[] data, string mime) => Json(new JsonObject
    {
        ["candidates"] = new JsonArray(new JsonObject
        {
            ["content"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["inlineData"] = new JsonObject { ["mimeType"] = mime, ["data"] = Convert.ToBase64String(data) }
                })
            }
        })
    }.ToJsonString());
}
