using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.Wave;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class NarrationExportTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "cohost-export-tests-" + Guid.NewGuid().ToString("N"));
    internal static byte[] Wave()
    {
        var pcm = new byte[24000 * 2 * 2];
        for (int frame = 24000 / 2; frame < 24000; frame++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 2), (short)(Math.Sin(frame * 2 * Math.PI * 440 / 24000) * 16000));
        using var buffer = new MemoryStream();
        using (var writer = new WaveFileWriter(buffer, new WaveFormat(24000, 16, 1))) writer.Write(pcm, 0, pcm.Length);
        return buffer.ToArray();
    }
    private NarrationJobRequest Request() => new() { Text = "Test script", Output = Path.Combine(temporary, "café clip &(sample).webm"), Size = 512 };

    [Fact]
    public void OfflineAnimationUsesAudioTimeAndExporterDoesNotChangeSpoutDefaults()
    {
        var request = Request();
        var scene = VideoExportScene.FromWave(Wave(), request);
        Assert.Equal(60, scene.FrameCount); Assert.Equal(2000, scene.DurationMs);
        Assert.Equal(0, scene.LevelAt(0)); Assert.True(scene.LevelAt(20) > .2f); Assert.Equal(0, scene.LevelAt(55));
        var starter = new AvatarPackLibrary().Starter;
        var first = new AvatarAnimator(); var second = new AvatarAnimator();
        for (int frame = 0; frame < scene.FrameCount; frame++)
        {
            var activity = new PlaybackActivity(1, scene.LevelAt(frame), true);
            Assert.Equal(first.Next(starter, activity, "Normal", 50, 0, 0, frame * 1000 / 30), second.Next(starter, activity, "Normal", 50, 0, 0, frame * 1000 / 30));
        }
        using var spout = new SpoutFrameRenderer(starter);
        using var export = new SpoutFrameRenderer(starter, 1080, 0xFF00FF00);
        var snapshot = first.Next(starter, new(1, 0, true), "Normal", 50, 0, 0, 2100);
        spout.Render(snapshot); export.Render(snapshot);
        Assert.Equal(0, spout.Pixels.Span[3]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, export.Pixels.Span[..4].ToArray());
        Assert.Equal(1080 * 1080 * 4, export.Pixels.Length);
    }

    [Fact]
    public void EncoderArgumentsAreSeparatedAndNeverContainScriptOrCredentials()
    {
        var scene = VideoExportScene.FromWave(Wave(), Request());
        var options = new VideoExportOptions("result &(clip).webm", 512, false, new(Path.Combine(temporary, "ffmpeg.exe"), "ffprobe.exe"));
        var arguments = NarrationExporter.EncoderStartInfo(scene, options, "audio path.wav", "video path.webm");
        Assert.False(arguments.UseShellExecute); Assert.True(arguments.RedirectStandardInput); Assert.True(arguments.RedirectStandardOutput);
        Assert.Contains("audio path.wav", arguments.ArgumentList); Assert.Contains("video path.webm", arguments.ArgumentList);
        Assert.Contains("libvpx-vp9", arguments.ArgumentList); Assert.Contains("libopus", arguments.ArgumentList);
        Assert.DoesNotContain("Test script", arguments.ArgumentList);
        Assert.Equal("", arguments.Arguments);
    }

    [Fact]
    public async Task ExistingDestinationAndMissingFfmpegFailBeforeNetwork()
    {
        Directory.CreateDirectory(temporary);
        var request = Request();
        await File.WriteAllTextAsync(request.Output, "original", TestContext.Current.CancellationToken);
        var exporter = new NarrationExporter();
        var existing = await Assert.ThrowsAsync<CliException>(() => exporter.PrepareAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(CliExitCode.InvalidRequest, existing.Code);
        var missing = await Assert.ThrowsAsync<CliException>(() => exporter.PrepareAsync(request with { Overwrite = true, FfmpegPath = Path.Combine(temporary, "missing.exe") }, TestContext.Current.CancellationToken));
        Assert.Equal(CliExitCode.Dependency, missing.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(request.Output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RealNetworkFreeWebmContainsGreenFramesMouthAnimationAndOpus()
    {
        if (!File.Exists(NarrationExporter.DefaultFfmpegPath)) Assert.Skip("Local ignored FFmpeg bundle is required for the encoder integration check.");
        var request = Request(); var exporter = new NarrationExporter();
        var scene = VideoExportScene.FromWave(Wave(), request);
        var options = await exporter.PrepareAsync(request, TestContext.Current.CancellationToken);
        var stages = new List<string>();
        var video = await exporter.ExportAsync(new AvatarPackLibrary().Starter, scene, options, new InlineProgress<JobProgress>(value => stages.Add(value.Stage)), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(video.Output)); Assert.InRange(video.DurationMs, 1950, 2100);
        Assert.Contains("rendering", stages); Assert.Contains("validating", stages); Assert.Contains("completed", stages);
        byte[] idle = await DecodeFrame(options.Tools.Executable, video.Output, .1);
        byte[] talking = await DecodeFrame(options.Tools.Executable, video.Output, .75);
        Assert.Equal(512 * 512 * 3, idle.Length); Assert.Equal(idle.Length, talking.Length);
        Assert.InRange(idle[0], (byte)0, (byte)8); Assert.InRange(idle[1], (byte)245, (byte)255); Assert.InRange(idle[2], (byte)0, (byte)8);
        Assert.False(idle.SequenceEqual(talking));
        Assert.Single(Directory.GetFiles(temporary));
    }

    [Fact]
    public async Task CancellationAndEncodingFailureCleanTemporaryFilesAndPreserveDestination()
    {
        if (!File.Exists(NarrationExporter.DefaultFfmpegPath)) Assert.Skip("Local ignored FFmpeg bundle is required.");
        var request = Request(); var exporter = new NarrationExporter();
        Directory.CreateDirectory(temporary);
        await File.WriteAllTextAsync(request.Output, "original", TestContext.Current.CancellationToken);
        var options = await exporter.PrepareAsync(request with { Overwrite = true }, TestContext.Current.CancellationToken);
        var scene = VideoExportScene.FromWave(Wave(), request);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var before = Directory.GetDirectories(Path.GetTempPath(), "ContentCoHostStudio-export-*").ToHashSet();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(new AvatarPackLibrary().Starter, scene, options,
            new InlineProgress<JobProgress>(value => { if (value.Stage == "rendering") stop.Cancel(); }), stop.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(request.Output, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(temporary));
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetTempPath(), "ContentCoHostStudio-export-*"), path => !before.Contains(path));
        var failed = await Assert.ThrowsAsync<CliException>(() => exporter.ExportAsync(new AvatarPackLibrary().Starter, scene,
            options with { Tools = new(Path.Combine(temporary, "missing encoder.exe"), "missing probe") }, null, TestContext.Current.CancellationToken));
        Assert.Equal(CliExitCode.Encoding, failed.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(request.Output, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(temporary));
    }

    private static async Task<byte[]> DecodeFrame(string executable, string path, double seconds)
    {
        var start = ExportProcess.StartInfo(executable);
        foreach (string argument in new[] { "-v", "error", "-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var stop = TestContext.Current.CancellationToken.Register(() => ExportProcess.Kill(process));
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output, TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await errors;
        Assert.Equal(0, process.ExitCode);
        return output.ToArray();
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(temporary);
        if (!full.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "cohost-export-tests-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
