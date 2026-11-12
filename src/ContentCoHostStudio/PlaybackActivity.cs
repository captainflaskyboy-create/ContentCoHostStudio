using System.Buffers.Binary;
using NAudio.Wave;

namespace ContentCoHostStudio;

public sealed record PlaybackActivity(long Id, float Level, bool Active);
public interface IPlaybackActivitySource
{
    PlaybackActivity Current { get; }
    event Action<PlaybackActivity>? Changed;
}

public sealed class PlaybackActivityHub : IPlaybackActivitySource
{
    private readonly object gate = new();
    private PlaybackActivity current = new(0, 0, false);
    public PlaybackActivity Current { get { lock (gate) return current; } }
    public event Action<PlaybackActivity>? Changed;
    public long Begin()
    {
        lock (gate)
        {
            current = new(current.Id + 1, 0, true);
            Changed?.Invoke(current);
            return current.Id;
        }
    }
    public void Report(long id, float level)
    {
        lock (gate)
        {
            if (id != current.Id || !current.Active) return;
            current = current with { Level = float.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0 };
            Changed?.Invoke(current);
        }
    }
    public void Stop(long id)
    {
        lock (gate)
        {
            if (id != current.Id) return;
            current = current with { Level = 0, Active = false };
            Changed?.Invoke(current);
        }
    }
    public void Reset()
    {
        lock (gate) { current = new(current.Id + 1, 0, false); Changed?.Invoke(current); }
    }
}

// RMS windows are indexed by output bytes. Reading/queueing sound never animates it early.
public sealed class PlaybackMeter(IWaveProvider source) : IWaveProvider
{
    private readonly object gate = new();
    private readonly Queue<(long Start, long End, float Level)> windows = new();
    private long submitted;
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(byte[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        int window = Math.Max(2, WaveFormat.AverageBytesPerSecond / 50 / 2 * 2);
        lock (gate)
        {
            for (int start = 0; start < read; start += window)
            {
                int length = Math.Min(window, read - start);
                double sum = 0;
                for (int i = 0; i + 1 < length; i += 2)
                {
                    float sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset + start + i, 2)) / 32768f;
                    sum += sample * sample;
                }
                windows.Enqueue((submitted + start, submitted + start + length, (float)Math.Sqrt(sum / Math.Max(1, length / 2))));
            }
            submitted += read;
            while (windows.Count > 512) windows.Dequeue();
        }
        return read;
    }
    public float LevelAt(long playedBytes)
    {
        lock (gate)
        {
            while (windows.TryPeek(out var window) && window.End <= playedBytes) windows.Dequeue();
            return windows.TryPeek(out var current) && playedBytes >= current.Start ? current.Level : 0;
        }
    }
}

internal sealed class PlaybackActivityMonitor : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly PlaybackActivityHub hub;
    private readonly long id;
    private int disposed;
    public PlaybackActivityMonitor(PlaybackActivityHub hub, PlaybackMeter meter, Func<long> playedBytes)
    {
        this.hub = hub;
        id = hub.Begin();
        CancellationToken token = cancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) hub.Report(id, meter.LevelAt(playedBytes()));
            }
            catch (Exception) { /* A cancelled/disposed output cannot publish new activity. */ }
            finally { hub.Stop(id); }
        });
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancellation.Cancel(); hub.Stop(id); cancellation.Dispose();
    }
}
