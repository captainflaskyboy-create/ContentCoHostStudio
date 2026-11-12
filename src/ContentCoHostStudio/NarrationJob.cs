using System.Text.Json.Serialization;

namespace ContentCoHostStudio;

public enum NarrationMode { Narrate, Reply }
public enum CliExitCode { Success = 0, InvalidRequest = 2, Dependency = 3, Generation = 4, Encoding = 5, Internal = 6, Cancelled = 130 }
public sealed class CliException(CliExitCode code, string message) : Exception(message)
{
    public CliExitCode Code { get; } = code;
}

public sealed record NarrationJobRequest
{
    [JsonIgnore] public NarrationMode Mode { get; init; }
    public string? Text { get; init; }
    public string? Context { get; init; }
    public string Personality { get; init; } = GeminiService.DefaultPersonality;
    public string Output { get; init; } = "";
    public string AvatarId { get; init; } = AvatarPackLibrary.StarterId;
    public string Pose { get; init; } = "Normal";
    public string Voice { get; init; } = GeminiSpeechService.DefaultVoice;
    public string VoiceStyle { get; init; } = GeminiSpeechService.DroidStyle;
    public string TextModel { get; init; } = GeminiService.Model;
    public string SpeechModel { get; init; } = GeminiSpeechService.Model;
    public int Size { get; init; } = 1080;
    public int Sensitivity { get; init; } = 50;
    public int IdleBob { get; init; }
    public int Shake { get; init; }
    public bool Overwrite { get; init; }
    public bool ReturnText { get; init; }
    public string? FfmpegPath { get; init; }
}

public sealed record JobProgress(string Stage, int? Percent = null);
public sealed record NarrationJobResult(string Output, int DurationMs, int Width, int Height, int Fps, string Background,
    string VideoCodec, string AudioCodec, string AvatarId, string AvatarRevision, string? TextModel, string SpeechModel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SpokenText);

public sealed class NarrationJobRunner(HttpClient client, string apiKey, AvatarPackLibrary? library = null, INarrationExporter? exporter = null)
{
    private readonly AvatarPackLibrary library = library ?? new AvatarPackLibrary();
    private readonly INarrationExporter exporter = exporter ?? new NarrationExporter();

    public async Task<NarrationJobResult> RunAsync(NarrationJobRequest request, CancellationToken cancellationToken, IProgress<JobProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl))
            throw new CliException(CliExitCode.InvalidRequest, "Set GEMINI_API_KEY to a valid key in the calling process environment.");
        progress?.Report(new("preflight"));
        AvatarPack pack;
        try
        {
            await Task.Run(library.LoadInstalled, cancellationToken).ConfigureAwait(false);
            pack = library.Find(request.AvatarId) ?? throw new CliException(CliExitCode.InvalidRequest, "The requested avatar is not installed. Use avatars list to find an ID.");
        }
        catch (CliException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new CliException(CliExitCode.Dependency, "Could not read the avatar library. Check artwork and folder permissions."); }
        if (!pack.Catalog.Available(request.Pose))
            throw new CliException(CliExitCode.InvalidRequest, "The selected avatar does not have that pose. Use avatars list to find available poses.");
        var options = await exporter.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        string text = request.Text ?? "";
        if (request.Mode == NarrationMode.Reply)
        {
            progress?.Report(new("text-generation"));
            try { text = await new GeminiService(client).GenerateAsync(apiKey, request.Personality, request.Context!, cancellationToken, request.TextModel).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { throw new CliException(CliExitCode.Generation, "Text generation failed. Check the selected model, key, quota, and network. No retry or fallback was attempted."); }
        }
        progress?.Report(new("speech-generation"));
        byte[] wav;
        try { wav = await new GeminiSpeechService(client, apiKey).SynthesizeAsync(text, request.Voice, request.VoiceStyle, cancellationToken, request.SpeechModel).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new CliException(CliExitCode.Generation, "Speech generation failed. Check the selected TTS model, key, quota, and network. No retry or fallback was attempted."); }
        progress?.Report(new("audio-analysis"));
        VideoExportScene scene;
        try { scene = await Task.Run(() => VideoExportScene.FromWave(wav, request), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new CliException(CliExitCode.Generation, "Generated speech was not valid supported audio."); }
        var video = await exporter.ExportAsync(pack, scene, options, progress, cancellationToken).ConfigureAwait(false);
        return new(video.Output, video.DurationMs, request.Size, request.Size, 30, "#00FF00", "vp9", "opus", pack.Id, pack.Revision,
            request.Mode == NarrationMode.Reply ? request.TextModel : null, request.SpeechModel, request.ReturnText ? text : null);
    }

    public static void Validate(NarrationJobRequest request)
    {
        bool narrate = request.Mode == NarrationMode.Narrate;
        if ((narrate && (string.IsNullOrWhiteSpace(request.Text) || !string.IsNullOrEmpty(request.Context))) ||
            (!narrate && (string.IsNullOrWhiteSpace(request.Context) || !string.IsNullOrEmpty(request.Text))))
            throw new CliException(CliExitCode.InvalidRequest, "Use text for narrate, or context for reply, without mixing the two inputs.");
        if (request.Size is not (512 or 1080) || request.Sensitivity is < 0 or > 100 || request.IdleBob is < 0 or > 100 || request.Shake is < 0 or > 100)
            throw new CliException(CliExitCode.InvalidRequest, "Size must be 512 or 1080; sensitivity, idleBob, and shake must be between 0 and 100.");
        if (!GeminiSpeechService.Voices.Contains(request.Voice) || request.VoiceStyle == null || request.Personality == null ||
            string.IsNullOrWhiteSpace(request.AvatarId) || string.IsNullOrWhiteSpace(request.Pose))
            throw new CliException(CliExitCode.InvalidRequest, "Provide a supported voice and valid avatar, pose, personality, and voiceStyle values.");
        if (request.SpeechModel == null || GeminiModelCatalog.GetSpeechProtocol(request.SpeechModel) == SpeechProtocol.Unsupported)
            throw new CliException(CliExitCode.InvalidRequest, "Choose a supported TTS model. Use models list for available options.");
        try { if (!narrate) GeminiService.EndpointFor(request.TextModel); }
        catch (Exception) { throw new CliException(CliExitCode.InvalidRequest, "Choose a compatible text model for reply jobs."); }
        if (string.IsNullOrWhiteSpace(request.Output) || !Path.GetExtension(request.Output).Equals(".webm", StringComparison.OrdinalIgnoreCase))
            throw new CliException(CliExitCode.InvalidRequest, "Output must name a .webm file.");
    }
}

internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
