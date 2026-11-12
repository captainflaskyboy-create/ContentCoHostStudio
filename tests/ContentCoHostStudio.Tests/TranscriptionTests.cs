using Xunit;

namespace ContentCoHostStudio.Tests;

public class TranscriptionTests
{
    [Fact]
    public async Task MissingModelReportsActionableErrorAndCanRestart()
    {
        var service = new TranscriptionService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var errors = new List<string>();
        service.Error += errors.Add;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStatus(string status)
            {
                if (status == "Stopped")
                    stopped.TrySetResult();
            }
            service.Status += OnStatus;
            service.Start(attempt);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await service.StopAsync();
            service.Status -= OnStatus;
            Assert.False(service.IsRunning);
        }
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Contains(OperatingSystem.IsWindows() ? "model is missing" : "Windows only", error));
    }

    [Fact]
    public void PackagedWindowsVoskRuntimeLoadsWithoutASeparateInstaller()
    {
        if (OperatingSystem.IsWindows() && Environment.Is64BitProcess)
            Vosk.Vosk.SetLogLevel(-1);
    }
}
