using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;

namespace ContentCoHostStudio;

public sealed class GeminiSpeechService(HttpClient client, string apiKey)
{
    public const string Model = "gemini-3.8-flash-tts";
    public static readonly Uri Endpoint = new("https://generativelanguage.googleapis.com/v1beta/interactions");
    public static readonly string[] Voices = ["Kore", "Puck", "Charon", "Fenrir"];
    public const string DefaultVoice = "Puck";
    public const string DroidStyle = "A crisp, articulate male baritone with a dry, slightly metallic resonance and a refined mid-Atlantic accent. " +
        "The tone is deadpan, unhurried, and emotionally detached—sounding like an exhausted, hyper-intelligent droid who finds everything mildly beneath him.\n" +
        "Clear enunciation, low vocal warmth, and a naturally flat, matter-of-fact cadence.";
    public const string RobotStyle = "Dry, deadpan delivery with clipped, precise articulation, subtle ironic emphasis, and friendly teasing. Sound like a sarcastic robotic co-host, without metallic sound effects.";
    public const string DocumentaryStyle = "Reflective documentary narration with restrained sadness, measured pacing, and clear articulation. Sound quietly melancholy, not theatrical or whispered.";
    private const int MaximumAudioBytes = 24 * 1024 * 1024;

    public static JsonObject BuildPayload(string text, string voice, string style, string model = Model) => new()
    {
        ["model"] = model,
        ["store"] = false,
        ["input"] = new JsonArray(new JsonObject
        {
            ["type"] = "user_input",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
                ["annotations"] = new JsonArray(new JsonObject { ["type"] = "speech_metadata", ["style"] = style })
            })
        }),
        ["response_format"] = new JsonObject { ["type"] = "audio" },
        ["generation_config"] = new JsonObject
        {
            ["speech_config"] = new JsonArray(new JsonObject { ["voice"] = voice })
        }
    };

    public static JsonObject BuildLegacyPayload(string text, string voice, string style) => new()
    {
        ["contents"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray(new JsonObject
            {
                ["text"] = $"Read the transcript below verbatim. Use this delivery style: {style}\nDo not read these directions aloud.\n\nTranscript:\n{text}"
            })
        }),
        ["generationConfig"] = new JsonObject
        {
            ["responseModalities"] = new JsonArray("AUDIO"),
            ["speechConfig"] = new JsonObject
            {
                ["voiceConfig"] = new JsonObject
                {
                    ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = voice }
                }
            }
        }
    };

    public async Task<byte[]> SynthesizeAsync(string text, string voice, string style, CancellationToken cancellationToken, string model = Model)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl))
            throw new InvalidOperationException("Enter a valid Gemini API key to prepare a voice.");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Generate a reply before speaking it.");
        if (!Voices.Contains(voice))
            throw new InvalidOperationException("Choose one of the supported voices.");
        SpeechProtocol protocol = GeminiModelCatalog.GetSpeechProtocol(model);
        if (protocol == SpeechProtocol.Unsupported)
            throw new InvalidOperationException("This speech model's protocol is not supported by the POC. Choose another TTS model.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        Uri endpoint = protocol == SpeechProtocol.InteractionsWav ? Endpoint :
            new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("x-goog-api-key", apiKey.Trim());
        request.Content = JsonContent.Create(protocol == SpeechProtocol.InteractionsWav ? BuildPayload(text, voice, style, model) : BuildLegacyPayload(text, voice, style));
        try
        {
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(ErrorForStatus(response.StatusCode));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
            return protocol == SpeechProtocol.InteractionsWav ? ReadAudio(document.RootElement) : ReadLegacyAudio(document.RootElement);
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Could not reach Gemini speech. Check your internet connection and try again.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Gemini speech took too long. Try again shortly.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Gemini speech returned an unreadable response. Try again.");
        }
    }

    public static string ErrorForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => "Gemini speech rejected the request. Check your key, voice settings, and speech model access.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Gemini speech authentication failed. Check your API key and its permissions.",
        HttpStatusCode.NotFound => "The selected speech model is unavailable to this account. Refresh the model list or choose another TTS model.",
        HttpStatusCode.TooManyRequests => "Gemini speech quota or rate limit was reached. Wait and check your account quota.",
        HttpStatusCode.ServiceUnavailable => "Gemini speech is temporarily unavailable or in high demand. Try again shortly.",
        _ => "Gemini speech could not complete the request. Try again later."
    };

    public static byte[] ReadAudio(JsonElement root)
    {
        try
        {
            JsonElement? audio = null;
            if (root.TryGetProperty("steps", out var steps))
            {
                foreach (var step in steps.EnumerateArray())
                {
                    if (step.GetProperty("type").GetString() != "model_output" || !step.TryGetProperty("content", out var content))
                        continue;
                    foreach (var part in content.EnumerateArray())
                        if (part.GetProperty("type").GetString() == "audio")
                            audio = part;
                }
            }
            if (audio is not { } block || !block.TryGetProperty("data", out var data))
                throw new FormatException();
            if (block.TryGetProperty("mime_type", out var mime) && mime.GetString() is not ("audio/wav" or "audio/x-wav"))
                throw new FormatException();
            string encoded = data.GetString() ?? "";
            if (encoded.Length == 0 || encoded.Length > MaximumAudioBytes * 4 / 3 + 4)
                throw new FormatException();
            byte[] wav = Convert.FromBase64String(encoded);
            ValidateWav(wav);
            return wav;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Gemini speech returned missing, blocked, or invalid WAV audio. Try again.");
        }
    }

    public static byte[] ReadLegacyAudio(JsonElement root)
    {
        try
        {
            var candidate = root.GetProperty("candidates")[0];
            if (candidate.TryGetProperty("finishReason", out var reason) &&
                reason.GetString() is "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "RECITATION" or "SPII")
                throw new FormatException();
            JsonElement? audio = null;
            foreach (var part in candidate.GetProperty("content").GetProperty("parts").EnumerateArray())
                if (part.TryGetProperty("inlineData", out var inline))
                    audio = inline;
            if (audio is not { } block)
                throw new FormatException();
            string encoded = block.GetProperty("data").GetString() ?? "";
            if (encoded.Length == 0 || encoded.Length > MaximumAudioBytes * 4 / 3)
                throw new FormatException();
            byte[] data = Convert.FromBase64String(encoded);
            string mime = block.GetProperty("mimeType").GetString() ?? "";
            if (mime.Equals("audio/wav", StringComparison.OrdinalIgnoreCase) || mime.Equals("audio/x-wav", StringComparison.OrdinalIgnoreCase))
            {
                ValidateWav(data);
                return data;
            }
            string[] mimeParts = mime.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (mimeParts.Length == 0 || !mimeParts[0].Equals("audio/l16", StringComparison.OrdinalIgnoreCase) ||
                data.Length == 0 || data.Length % 2 != 0)
                throw new FormatException();
            foreach (string parameter in mimeParts.Skip(1))
            {
                if (parameter.StartsWith("rate=", StringComparison.OrdinalIgnoreCase) && parameter != "rate=24000" ||
                    parameter.StartsWith("channels=", StringComparison.OrdinalIgnoreCase) && parameter != "channels=1" ||
                    parameter.StartsWith("codec=", StringComparison.OrdinalIgnoreCase) && !parameter.Equals("codec=pcm", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException();
            }
            using var stream = new MemoryStream();
            using (var writer = new WaveFileWriter(stream, new WaveFormat(24000, 16, 1)))
                writer.Write(data, 0, data.Length);
            byte[] wav = stream.ToArray();
            ValidateWav(wav);
            return wav;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            throw new InvalidOperationException("The speech model returned missing, blocked, or unsupported audio. Try another TTS model.");
        }
    }

    public static void ValidateWav(byte[] wav)
    {
        try
        {
            if (wav.Length < 44 || wav.Length > MaximumAudioBytes ||
                !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                throw new FormatException();
            if (BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4, 4)) + 8L > wav.Length)
                throw new FormatException();
            using var stream = new MemoryStream(wav, writable: false);
            using var reader = new WaveFileReader(stream);
            if (reader.Length == 0 || reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
                reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1 ||
                reader.WaveFormat.SampleRate != 24000 || reader.Length % reader.WaveFormat.BlockAlign != 0)
                throw new FormatException();
        }
        catch (Exception exception) when (exception is FormatException or IOException or ArgumentException)
        {
            throw new InvalidOperationException("Gemini speech returned invalid WAV audio. Try again.");
        }
    }
}
