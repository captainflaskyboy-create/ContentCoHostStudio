using System.Text.Json;
using System.Text.RegularExpressions;

namespace ContentCoHostStudio;

public enum GeminiModelKind { Text, Speech, Live }
public enum SpeechProtocol { Unsupported, InteractionsWav, LegacyPcm }
public enum LiveProtocol { Unsupported, NativeAudio, ProactiveNativeAudio }

public sealed record GeminiModelOption(string Id, GeminiModelKind Kind, bool IsSupported,
    string? Explanation = null, bool MissingFromCatalog = false)
{
    public bool IsPreview => Id.Contains("preview", StringComparison.OrdinalIgnoreCase) || Id.Contains("exp", StringComparison.OrdinalIgnoreCase);
    public string Label => Id + (IsPreview ? " [preview/experimental]" : "") +
        (MissingFromCatalog ? " [not in latest catalog]" : "") + (!IsSupported ? " [unsupported]" : "");
}

public sealed class GeminiModelCatalog(HttpClient client)
{
    public static readonly Uri Endpoint = new("https://generativelanguage.googleapis.com/v1beta/models");

    public static bool IsValidId(string id) => Regex.IsMatch(id, @"^gemini-[a-zA-Z0-9._-]+$");

    public static LiveProtocol GetLiveProtocol(string id)
    {
        if (!IsValidId(id)) return LiveProtocol.Unsupported;
        if (id is "gemini-3.8-live" or "gemini-3.1-flash-live-preview") return LiveProtocol.NativeAudio;
        if (id.StartsWith("gemini-2.5-flash-native-audio-", StringComparison.Ordinal)) return LiveProtocol.ProactiveNativeAudio;
        return LiveProtocol.Unsupported;
    }

    public static string LiveExplanation(string id) => id switch
    {
        "gemini-3.8-live" => "Proactive audio is permanently enabled. Access depends on your key and quota.",
        "gemini-3.1-flash-live-preview" => "Legacy preview. Proactive audio is not supported; it may respond more often during narration.",
        "gemini-3.8-live-extended-thinking" => "Not supported: this model needs asynchronous interaction-status handling that this POC does not implement.",
        _ => GetLiveProtocol(id) == LiveProtocol.ProactiveNativeAudio ? "Legacy native-audio model. Proactive audio is enabled for selective replies. Access is not guaranteed." :
            "This model's Live protocol or capabilities are not supported by this POC."
    };

    public static SpeechProtocol GetSpeechProtocol(string id)
    {
        if (!IsValidId(id) || !id.Contains("-tts", StringComparison.Ordinal))
            return SpeechProtocol.Unsupported;
        if (id.StartsWith("gemini-3.8-", StringComparison.Ordinal))
            return SpeechProtocol.InteractionsWav;
        if (id.StartsWith("gemini-3.1-", StringComparison.Ordinal) || id.StartsWith("gemini-2.5-", StringComparison.Ordinal))
            return SpeechProtocol.LegacyPcm;
        return SpeechProtocol.Unsupported;
    }

    public static GeminiModelOption? Classify(string id, IEnumerable<string> methods)
    {
        if (!IsValidId(id))
            return null;
        bool canGenerate = methods.Contains("generateContent", StringComparer.OrdinalIgnoreCase);
        if (id.Contains("-tts", StringComparison.Ordinal))
        {
            bool supported = canGenerate && GetSpeechProtocol(id) != SpeechProtocol.Unsupported;
            return new(id, GeminiModelKind.Speech, supported, supported ? null : "This model's speech protocol is not supported by this POC.");
        }
        bool canLive = methods.Contains("bidiGenerateContent", StringComparer.OrdinalIgnoreCase);
        if (canLive || id.Contains("live", StringComparison.OrdinalIgnoreCase) || id.Contains("native-audio", StringComparison.OrdinalIgnoreCase))
        {
            bool supported = canLive && GetLiveProtocol(id) != LiveProtocol.Unsupported;
            return new(id, GeminiModelKind.Live, supported, canLive ? LiveExplanation(id) : "This catalog entry does not advertise bidiGenerateContent support.");
        }
        string[] excluded = ["embedding", "image", "native-audio", "live", "transcrib", "transcription", "audio", "robotics", "computer-use", "omni"];
        if (excluded.Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase)))
            return null;
        return new(id, GeminiModelKind.Text, canGenerate, canGenerate ? null : "This model does not advertise generateContent support.");
    }

    public async Task<IReadOnlyList<GeminiModelOption>> LoadAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl))
            throw new InvalidOperationException("Enter a valid API key before refreshing models.");
        var models = new Dictionary<string, GeminiModelOption>(StringComparer.Ordinal);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? nextPage = null;
        try
        {
            do
            {
                string query = "?pageSize=1000" + (nextPage == null ? "" : "&pageToken=" + Uri.EscapeDataString(nextPage));
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint + query));
                request.Headers.Add("x-goog-api-key", apiKey.Trim());
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Could not refresh models. " + GeminiService.ErrorForStatus(response.StatusCode));
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                nextPage = ReadPage(document.RootElement, models);
                if (!string.IsNullOrEmpty(nextPage) && !seenTokens.Add(nextPage))
                    throw new JsonException();
            } while (!string.IsNullOrEmpty(nextPage));
            return models.Values.OrderBy(model => model.Id, StringComparer.Ordinal).ToArray();
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Could not refresh models. Check your internet connection and try again.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Refreshing models took too long. Try again.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Google returned an unreadable model catalog. Try refreshing again.");
        }
    }

    private static string? ReadPage(JsonElement root, Dictionary<string, GeminiModelOption> models)
    {
        try
        {
            if (root.TryGetProperty("models", out var entries))
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    string name = entry.GetProperty("name").GetString() ?? "";
                    string id = name.StartsWith("models/", StringComparison.Ordinal) ? name[7..] : name;
                    string[] methods = entry.TryGetProperty("supportedGenerationMethods", out var actions) ?
                        actions.EnumerateArray().Select(action => action.GetString() ?? "").ToArray() : [];
                    if (Classify(id, methods) is { } option)
                        models[id] = option;
                }
            }
            return root.TryGetProperty("nextPageToken", out var token) ? token.GetString() : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            throw new JsonException();
        }
    }
}
