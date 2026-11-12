using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace ContentCoHostStudio;

public sealed record VideoExportScene(byte[] Wave, double DurationMs, float[] Levels, string Pose, int Sensitivity, int IdleBob, int Shake)
{
    public int FrameCount => (int)Math.Ceiling(DurationMs * 30 / 1000);
    public static VideoExportScene FromWave(byte[] wav, NarrationJobRequest request)
    {
        GeminiSpeechService.ValidateWav(wav);
        using var stream = new MemoryStream(wav, false);
        using var reader = new WaveFileReader(stream);
        var pcm = new byte[checked((int)reader.Length)];
        reader.ReadExactly(pcm);
        const int window = 24000 / 50 * 2;
        var levels = new float[(pcm.Length + window - 1) / window];
        for (int index = 0; index < levels.Length; index++)
        {
            int start = index * window, count = Math.Min(window, pcm.Length - start);
            double sum = 0;
            for (int i = 0; i < count; i += 2)
            {
                double value = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(start + i, 2)) / 32768d;
                sum += value * value;
            }
            levels[index] = (float)Math.Sqrt(sum / (count / 2));
        }
        return new(wav, pcm.Length / 48d, levels, request.Pose, request.Sensitivity, request.IdleBob, request.Shake);
    }
    public float LevelAt(int frame)
    {
        int index = Math.Min(Levels.Length - 1, (int)((long)frame * 1000 / 30 / 20));
        float peak = 0;
        for (int i = Math.Max(0, index - 2); i <= index; i++) peak = Math.Max(peak, Levels[i]);
        return peak;
    }
}

public sealed record FfmpegTools(string Executable, string ProbeExecutable);
public sealed record VideoExportOptions(string Destination, int Size, bool Overwrite, FfmpegTools Tools);
public sealed record VideoExportResult(string Output, int DurationMs);
public interface INarrationExporter
{
    Task<VideoExportOptions> PrepareAsync(NarrationJobRequest request, CancellationToken cancellationToken);
    Task<VideoExportResult> ExportAsync(AvatarPack pack, VideoExportScene scene, VideoExportOptions options,
        IProgress<JobProgress>? progress, CancellationToken cancellationToken);
}

public sealed class NarrationExporter : INarrationExporter
{
    public static string DefaultFfmpegPath => Path.Combine(AppContext.BaseDirectory, "native", "FFmpeg", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    public async Task<VideoExportOptions> PrepareAsync(NarrationJobRequest request, CancellationToken token)
    {
        string destination;
        try
        {
            destination = Path.GetFullPath(request.Output);
            if (Directory.Exists(destination) || (File.Exists(destination) && !request.Overwrite))
                throw new CliException(CliExitCode.InvalidRequest, "Output already exists or names a directory. Choose another destination, or explicitly enable overwrite for a file.");
        }
        catch (CliException) { throw; }
        catch (Exception) { throw new CliException(CliExitCode.InvalidRequest, "The output path is invalid."); }
        FfmpegTools tools;
        try
        {
            string executable = Path.GetFullPath(request.FfmpegPath ?? DefaultFfmpegPath);
            string probe = Path.Combine(Path.GetDirectoryName(executable)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (!File.Exists(executable) || !File.Exists(probe))
                throw new CliException(CliExitCode.Dependency, "FFmpeg and adjacent ffprobe are required. Supply ffmpegPath or the documented local native/FFmpeg tools.");
            string version = await ExportProcess.CaptureAsync(executable, ["-hide_banner", "-version"], token).ConfigureAwait(false);
            string probeVersion = await ExportProcess.CaptureAsync(probe, ["-hide_banner", "-version"], token).ConfigureAwait(false);
            if (!version.StartsWith("ffmpeg version ", StringComparison.OrdinalIgnoreCase) || !probeVersion.StartsWith("ffprobe version ", StringComparison.OrdinalIgnoreCase))
                throw new CliException(CliExitCode.Dependency, "The selected executables did not identify themselves as FFmpeg and ffprobe.");
            string encoders = await ExportProcess.CaptureAsync(executable, ["-hide_banner", "-encoders"], token).ConfigureAwait(false);
            var names = encoders.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A').Select(parts => parts[1]).ToHashSet();
            if (!names.Contains("libvpx-vp9") || !names.Contains("libopus"))
                throw new CliException(CliExitCode.Dependency, "This FFmpeg build needs libvpx-vp9 and libopus for narrated WebM export.");
            tools = new(executable, probe);
        }
        catch (CliException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new CliException(CliExitCode.Dependency, "Could not probe FFmpeg. Check the executable, adjacent ffprobe/runtime DLLs, and permissions."); }
        try
        {
            string directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            string check = Path.Combine(directory, ".cohost-write-check-" + Guid.NewGuid().ToString("N"));
            using var file = new FileStream(check, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception) { throw new CliException(CliExitCode.InvalidRequest, "The output folder cannot be written. Check the destination and permissions."); }
        token.ThrowIfCancellationRequested();
        return new(destination, request.Size, request.Overwrite, tools);
    }

    public async Task<VideoExportResult> ExportAsync(AvatarPack pack, VideoExportScene scene, VideoExportOptions options,
        IProgress<JobProgress>? progress, CancellationToken token)
    {
        string work = Path.Combine(Path.GetTempPath(), "ContentCoHostStudio-export-" + Guid.NewGuid().ToString("N"));
        string temporary = Path.Combine(Path.GetDirectoryName(options.Destination)!, "." + Path.GetFileNameWithoutExtension(options.Destination) + "." + Guid.NewGuid().ToString("N") + ".rendering.webm");
        try
        {
            Directory.CreateDirectory(work);
            string audio = Path.Combine(work, "narration.wav");
            await File.WriteAllBytesAsync(audio, scene.Wave, token).ConfigureAwait(false);
            await Task.Run(async () =>
            {
                using var renderer = new SpoutFrameRenderer(pack, options.Size, 0xFF00FF00);
                var animator = new AvatarAnimator();
                using var encoder = Process.Start(EncoderStartInfo(scene, options, audio, temporary)) ?? throw new IOException();
                using var stop = token.Register(() => ExportProcess.Kill(encoder));
                var errors = ExportProcess.DrainAsync(encoder.StandardError);
                var output = ExportProcess.DrainAsync(encoder.StandardOutput);
                try
                {
                    int lastPercent = -1;
                    for (int frame = 0; frame < scene.FrameCount; frame++)
                    {
                        token.ThrowIfCancellationRequested();
                        var snapshot = animator.Next(pack, new(1, scene.LevelAt(frame), true), scene.Pose,
                            scene.Sensitivity, scene.IdleBob, scene.Shake, (long)frame * 1000 / 30);
                        renderer.Render(snapshot);
                        await encoder.StandardInput.BaseStream.WriteAsync(renderer.Pixels, token).ConfigureAwait(false);
                        int percent = (frame + 1) * 100 / scene.FrameCount;
                        if (percent != lastPercent) { lastPercent = percent; progress?.Report(new("rendering", percent)); }
                    }
                    encoder.StandardInput.Close();
                    await encoder.WaitForExitAsync(token).ConfigureAwait(false);
                    await errors.ConfigureAwait(false);
                    await output.ConfigureAwait(false);
                    if (encoder.ExitCode != 0) throw new IOException();
                }
                finally
                {
                    ExportProcess.Kill(encoder);
                    try { await encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
                    try { await Task.WhenAll(errors, output).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
                }
            }, token).ConfigureAwait(false);
            progress?.Report(new("validating"));
            int duration = await ValidateAsync(temporary, scene, options, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, options.Destination, options.Overwrite);
            progress?.Report(new("completed", 100));
            return new(options.Destination, duration);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (CliException) { throw; }
        catch (Exception) { throw new CliException(CliExitCode.Encoding, "WebM rendering or validation failed. Check FFmpeg, available disk space, and destination permissions. Existing output was not replaced unless publication completed."); }
        finally
        {
            // These unique paths belong only to this invocation, never to a caller's source files.
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                string full = Path.GetFullPath(work);
                if (full.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "ContentCoHostStudio-export-"), StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                    Directory.Delete(full, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { progress?.Report(new("cleanup-incomplete")); }
        }
    }

    internal static ProcessStartInfo EncoderStartInfo(VideoExportScene scene, VideoExportOptions options, string audio, string destination)
    {
        var start = ExportProcess.StartInfo(options.Tools.Executable);
        start.RedirectStandardInput = true;
        foreach (string argument in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{options.Size}x{options.Size}",
            "-framerate", "30", "-i", "pipe:0", "-i", audio, "-map", "0:v:0", "-map", "1:a:0", "-frames:v", scene.FrameCount.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-auto-alt-ref", "0", "-crf", "18", "-b:v", "0", "-c:a", "libopus", "-ar", "48000", "-ac", "1", "-b:a", "96k", "-f", "webm", destination })
            start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task<int> ValidateAsync(string file, VideoExportScene scene, VideoExportOptions options, CancellationToken token)
    {
        string json = await ExportProcess.CaptureAsync(options.Tools.ProbeExecutable,
            ["-v", "error", "-show_entries", "stream=codec_name,codec_type,width,height,pix_fmt,r_frame_rate,sample_rate,channels:stream_tags=alpha_mode:format=format_name,duration", "-of", "json", file], token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
        var audio = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "audio");
        var format = root.GetProperty("format");
        double duration = double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        bool alpha = video.TryGetProperty("tags", out var tags) && tags.TryGetProperty("alpha_mode", out var value) && value.GetString() == "1";
        if (streams.Length != 2 || video.GetProperty("codec_name").GetString() != "vp9" || audio.GetProperty("codec_name").GetString() != "opus" ||
            video.GetProperty("width").GetInt32() != options.Size || video.GetProperty("height").GetInt32() != options.Size ||
            video.GetProperty("pix_fmt").GetString() != "yuv420p" || video.GetProperty("r_frame_rate").GetString() != "30/1" || alpha ||
            audio.GetProperty("sample_rate").GetString() != "48000" || audio.GetProperty("channels").GetInt32() != 1 ||
            !format.GetProperty("format_name").GetString()!.Contains("webm", StringComparison.Ordinal) ||
            !double.IsFinite(duration) || duration <= 0 || Math.Abs(duration - scene.DurationMs / 1000) > .1)
            throw new InvalidDataException();
        return checked((int)Math.Round(duration * 1000));
    }
}

internal static class ExportProcess
{
    public static ProcessStartInfo StartInfo(string executable) => new(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
    };
    public static async Task<string> CaptureAsync(string executable, string[] arguments, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        var start = StartInfo(executable);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException();
        using var cancel = limit.Token.Register(() => Kill(process));
        var output = DrainAsync(process.StandardOutput);
        var errors = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            string result = await output.ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException();
            return result;
        }
        finally
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
            try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
        }
    }
    public static async Task<string> DrainAsync(StreamReader reader)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            if (result.Length < 1024 * 1024) result.Append(buffer, 0, Math.Min(read, 1024 * 1024 - result.Length));
        return result.ToString();
    }
    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
