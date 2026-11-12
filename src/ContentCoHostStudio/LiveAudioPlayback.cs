using NAudio.Wave;

namespace ContentCoHostStudio;

public interface ILiveAudioPlayback : IDisposable
{
    int BufferedBytes { get; }
    void Append(byte[] pcm);
    void Interrupt();
}

public sealed class LiveAudioPlayback(bool muted = false, PlaybackActivityHub? activity = null) : ILiveAudioPlayback
{
    public const int MaximumBufferedBytes = 24000 * 2 * 30;
    private readonly object gate = new();
    private BufferedWaveProvider? buffer;
    private WaveOutEvent? output;
    private PlaybackActivityMonitor? monitor;
    private bool disposed;
    public int BufferedBytes { get { lock (gate) return buffer?.BufferedBytes ?? 0; } }

    public void Append(byte[] pcm)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pcm.Length % 2 != 0)
                throw new LiveSessionException("Live returned an incomplete PCM sample.");
            if (muted)
                return;
            if (!OperatingSystem.IsWindows())
                throw new LiveSessionException("Live playback supports Windows only in this POC.");
            if (output == null)
            {
                buffer = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
                {
                    BufferLength = MaximumBufferedBytes,
                    DiscardOnBufferOverflow = false,
                    ReadFully = true
                };
                output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3 };
                var meter = new PlaybackMeter(buffer);
                output.Init(meter);
                output.Play();
                if (activity != null) monitor = new PlaybackActivityMonitor(activity, meter, output.GetPosition);
            }
            if (pcm.Length > MaximumBufferedBytes - buffer!.BufferedBytes)
                throw new LiveSessionException("Live output exceeded the thirty-second audio buffer. The session was stopped.");
            buffer.AddSamples(pcm, 0, pcm.Length);
        }
    }

    public void Interrupt()
    {
        lock (gate)
        {
            // Releasing the device also clears samples already queued in the hardware buffers.
            monitor?.Dispose();
            monitor = null;
            output?.Stop();
            output?.Dispose();
            output = null;
            buffer?.ClearBuffer();
            buffer = null;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            Interrupt();
            disposed = true;
        }
    }
}
