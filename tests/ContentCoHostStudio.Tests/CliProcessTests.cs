using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class CliProcessTests
{
    [Theory]
    [InlineData("--help", 0)]
    [InlineData("invalid-command", 2)]
    public async Task SameGuiExecutableSupportsRedirectedCliAndStableExitCodes(string command, int expected)
    {
        var result = await RunAsync([command], "", null, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(expected == 0, json.RootElement.GetProperty("success").GetBoolean());
        Assert.Single(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task StdinUnicodeAndRequestFileProduceIdenticalStructuredErrorsWithoutEchoingSecrets()
    {
        const string request = "{\"text\":\"中文 café 🤖\",\"output\":\"clip.webm\"}";
        string file = Path.Combine(Path.GetTempPath(), "cohost-cli-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(file, request, new UTF8Encoding(false), TestContext.Current.CancellationToken);
            var pipe = await RunAsync(["narrate", "--request", "-"], request, null, TestContext.Current.CancellationToken);
            var disk = await RunAsync(["narrate", "--request", file], "", null, TestContext.Current.CancellationToken);
            Assert.Equal(2, pipe.Code); Assert.Equal(pipe.Output, disk.Output);
            Assert.DoesNotContain("中文", pipe.Output + pipe.Error);
            var avatars = await RunAsync(["avatars", "list"], "", null, TestContext.Current.CancellationToken);
            Assert.Equal(0, avatars.Code); Assert.Contains("cfb_avatar_pack", avatars.Output);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    [Trait("Category", "LiveCliExport")]
    public async Task OptInRealNarrateAndReplyProduceValidatedWebmUsingTheSameExecutable()
    {
        if (Environment.GetEnvironmentVariable("CONTENT_COHOST_RUN_CLI_EXPORT_TEST") != "1") Assert.Skip("Set CONTENT_COHOST_RUN_CLI_EXPORT_TEST=1 and GEMINI_API_KEY for two paid headless export checks.");
        string? key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) Assert.Skip("GEMINI_API_KEY is required.");
        string folder = Path.Combine(Path.GetTempPath(), "cohost-live-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (string command in new[] { "narrate", "reply" })
            {
                string destination = Path.Combine(folder, command + ".webm");
                string request = JsonSerializer.Serialize(new
                {
                    text = command == "narrate" ? "Well, that was almost professional." : null,
                    context = command == "reply" ? "The creator just dropped their script. Give one brief friendly robotic joke." : null,
                    personality = "You are a friendly robotic co-host. Reply with exactly one short sentence.",
                    output = destination,
                    size = 512,
                    textModel = Environment.GetEnvironmentVariable("CONTENT_COHOST_CLI_TEST_TEXT_MODEL") ?? GeminiService.Model,
                    speechModel = Environment.GetEnvironmentVariable("CONTENT_COHOST_CLI_TEST_SPEECH_MODEL") ?? GeminiSpeechService.Model
                });
                var result = await RunAsync([command, "--request", "-"], request, key, TestContext.Current.CancellationToken);
                Assert.True(result.Code == 0, $"{command} exited with code {result.Code}; the CLI returned a sanitized generation/export failure.");
                using var json = JsonDocument.Parse(result.Output);
                Assert.True(json.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("#00FF00", json.RootElement.GetProperty("result").GetProperty("background").GetString());
                Assert.True(File.Exists(destination)); Assert.True(new FileInfo(destination).Length > 1000);
                Assert.DoesNotContain(key, result.Output + result.Error);
            }
        }
        finally
        {
            string full = Path.GetFullPath(folder);
            if (!full.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "cohost-live-cli-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(full, true);
        }
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string[] arguments, string input, string? key, CancellationToken token)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ContentCoHostStudio.exe" : "ContentCoHostStudio");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment.Remove("GEMINI_API_KEY");
        if (key != null) start.Environment["GEMINI_API_KEY"] = key;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromMinutes(2));
        using var cancel = limit.Token.Register(() => ExportProcess.Kill(process));
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        await process.StandardInput.WriteAsync(input.AsMemory(), limit.Token); process.StandardInput.Close();
        await process.WaitForExitAsync(limit.Token);
        return (process.ExitCode, await output, await error);
    }
}
