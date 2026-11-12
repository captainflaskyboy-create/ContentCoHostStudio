using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContentCoHostStudio;

public sealed class GeminiService(HttpClient client)
{
    // Change the model here when Google retires or replaces it.
    public const string Model = "gemini-3.6-flash";
    public const string DefaultPersonality = "You are a sarcastic, funny but friendly robotic co-host for a YouTube creator. " +
        "React to the creator's transcript with a short, relevant reply. Use playful robot humor and gentle teasing. " +
        "Be supportive, avoid cruelty, and keep your response to a few sentences.";
    public static readonly Uri Endpoint = new($"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent");
    public static Uri EndpointFor(string model)
    {
        if (!GeminiModelCatalog.IsValidId(model) || GeminiModelCatalog.Classify(model, ["generateContent"])?.Kind != GeminiModelKind.Text)
            throw new InvalidOperationException("Choose a compatible text response model.");
        return new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
    }

    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(60),
        // Includes base64 speech audio; keep unexpected responses from using unbounded memory.
        MaxResponseContentBufferSize = 40 * 1024 * 1024
    };

    public static JsonObject BuildPayload(string personality, string transcript) => new()
    {
        ["systemInstruction"] = new JsonObject
        {
            ["parts"] = new JsonArray(new JsonObject { ["text"] = personality })
        },
        ["contents"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray(new JsonObject { ["text"] = transcript })
        })
    };

    public async Task<string> GenerateAsync(string apiKey, string personality, string transcript, CancellationToken cancellationToken, string model = Model)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(transcript))
            throw new InvalidOperationException("Enter an API key and a saved transcript first.");

        // Header validation also prevents accidental newline/header injection.
        if (apiKey.Any(char.IsControl))
            throw new InvalidOperationException("The API key contains invalid characters. Paste the key again.");

        using var request = new HttpRequestMessage(HttpMethod.Post, EndpointFor(model));
        request.Headers.Add("x-goog-api-key", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(BuildPayload(personality, transcript));

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(ErrorForStatus(response.StatusCode));

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return ReadResponse(document.RootElement);
        }
        catch (HttpRequestException)
        {
            // Never expose raw network errors or remote response bodies.
            throw new InvalidOperationException("Could not reach Gemini. Check your internet connection and try again.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Gemini took too long to respond. Try again.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Gemini returned an unreadable response. Try again.");
        }
    }

    public static string ErrorForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => "Gemini rejected the request. Check your API key and transcript size.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Gemini authentication failed. Check your API key and its permissions.",
        HttpStatusCode.TooManyRequests => "Gemini's rate limit or quota was reached. Wait before trying again, and check your quota.",
        HttpStatusCode.NotFound => "The selected Gemini model is unavailable. Refresh the model list or choose another model available to your account.",
        HttpStatusCode.ServiceUnavailable => "Gemini is temporarily unavailable or in high demand. Try again shortly.",
        _ => "Gemini could not complete the request. Try again later."
    };

    public static string ReadResponse(JsonElement root)
    {
        if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out _))
            throw new InvalidOperationException("Gemini blocked this request under its safety rules.");

        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            if (candidate.TryGetProperty("finishReason", out var reason) &&
                reason.GetString() is "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "RECITATION" or "SPII")
                throw new InvalidOperationException("Gemini could not provide a response under its content rules.");

            if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
            {
                var texts = new List<string>();
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                        continue;
                    if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        texts.Add(text.GetString() ?? "");
                }
                string answer = string.Join("", texts).Trim();
                if (answer.Length > 0)
                    return answer;
            }
        }

        throw new InvalidOperationException("Gemini returned no reply. Try a different transcript or personality prompt.");
    }
}
