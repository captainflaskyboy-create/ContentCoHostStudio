using NAudio.Wave;

namespace ContentCoHostStudio;

// Tests substitute a silent player, so ordinary test runs never use the speakers.
public interface ISpeechPlayback
{
    Task PlayAsync(byte[] wav, CancellationToken cancellationToken);
}

public sealed class SpeechPlayback(PlaybackActivityHub? activity = null) : ISpeechPlayback
{
    public async Task PlayAsync(byte[] wav, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Voice playback in this POC supports Windows only.");
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(wav, writable: false);
        using var reader = new WaveFileReader(stream);
        using var output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3 };
        var meter = new PlaybackMeter(reader);
        PlaybackActivityMonitor? monitor = null;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception != null)
                stopped.TrySetException(new InvalidOperationException("Could not play the voice. Check your default Windows output device."));
            else
                stopped.TrySetResult();
        };
        try
        {
            output.Init(meter);
            output.Play();
            if (activity != null) monitor = new PlaybackActivityMonitor(activity, meter, output.GetPosition);
            try
            {
                await stopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                monitor?.Dispose();
                // This continuation runs on a worker, including cancellation from the UI.
                output.Stop();
                await stopped.Task.ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            throw new InvalidOperationException("Could not play the voice. Check your default Windows output device and try again.");
        }
        finally { monitor?.Dispose(); }
    }
}
