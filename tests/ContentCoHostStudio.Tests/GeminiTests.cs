using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class GeminiTests
{
    [Fact]
    public void RequestSeparatesPersonalityFromUserTranscript()
    {
        var payload = GeminiService.BuildPayload("friendly robot", "my saved words");
        Assert.Equal("friendly robot", payload["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("user", payload["contents"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("my saved words", payload["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.False(payload.ContainsKey("tools"));
        Assert.False(payload.ContainsKey("safetySettings"));
    }

    [Fact]
    public async Task KeyIsOnlyInHeaderAndRequestUsesTranscriptSnapshot()
    {
        string transcript = "before edit";
        string? sentBody = null;
        using var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal("secret-test-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            Assert.DoesNotContain("secret-test-key", request.RequestUri!.ToString());
            sentBody = await request.Content!.ReadAsStringAsync(token);
            return JsonResponse("""{"candidates":[{"content":{"parts":[{"text":"Robot reply"}]}}]}""");
        });
        using var client = new HttpClient(handler);
        var service = new GeminiService(client);
        Task<string> pending = service.GenerateAsync("secret-test-key", "robot", transcript, CancellationToken.None);
        transcript = "after edit";
        Assert.Equal("Robot reply", await pending);
        Assert.DoesNotContain("secret-test-key", sentBody!);
        Assert.Contains("before edit", sentBody!);
        Assert.DoesNotContain(transcript, sentBody!);
    }

    [Theory]
    [InlineData(400, "API key")]
    [InlineData(401, "authentication")]
    [InlineData(403, "authentication")]
    [InlineData(404, "model")]
    [InlineData(429, "quota")]
    [InlineData(500, "later")]
    [InlineData(503, "high demand")]
    public async Task RemoteErrorsAreReadableAndCannotLeakRemoteBody(int status, string expected)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("server-echoes-secret-test-key")
        }));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GeminiService(client).GenerateAsync("secret-test-key", "robot", "hello", CancellationToken.None));
        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("secret-test-key", exception.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"candidates\":[]}")]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"SAFETY\"}]}")]
    public void EmptyAndBlockedRepliesProduceErrors(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidOperationException>(() => GeminiService.ReadResponse(document.RootElement));
    }

    [Fact]
    public void ResponseJoinsVisiblePartsAndExcludesThoughts()
    {
        using var document = JsonDocument.Parse("""{"candidates":[{"content":{"parts":[{"thought":true,"text":"internal"},{"text":"Hello "},{"text":"creator!"}]}}]}""");
        Assert.Equal("Hello creator!", GeminiService.ReadResponse(document.RootElement));
    }

    [Fact]
    public async Task NetworkErrorsDoNotExposeOriginalException()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("secret-test-key"));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GeminiService(client).GenerateAsync("secret-test-key", "robot", "hello", CancellationToken.None));
        Assert.Contains("internet", exception.Message);
        Assert.DoesNotContain("secret-test-key", exception.ToString());
    }

    [Fact]
    public async Task TimeoutIsReadableAndShutdownCancellationPropagates()
    {
        using var handler = new StubHandler((_, _) => throw new TaskCanceledException());
        using var client = new HttpClient(handler);
        var service = new GeminiService(client);
        var timeout = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync("key", "robot", "hello", CancellationToken.None));
        Assert.Contains("too long", timeout.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync("key", "robot", "hello", cancellation.Token));
    }

    [Theory]
    [InlineData("", "hello")]
    [InlineData("key", "  ")]
    [InlineData("key\nheader", "hello")]
    public async Task InvalidInputDoesNotSendRequests(string key, string transcript)
    {
        using var handler = new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Request should not be sent."));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GeminiService(client).GenerateAsync(key, "robot", transcript, CancellationToken.None));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
