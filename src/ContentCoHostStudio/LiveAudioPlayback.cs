using NAudio.Wave;

namespace ContentCoHostStudio;

public sealed record LivePlaybackTurn(long Id, bool Speaking);
public interface ILiveAudioPlayback : IDisposable
{
    int BufferedBytes { get; }
    bool IsSpeaking { get; }
    event Action<LivePlaybackTurn>? TurnChanged;
    event Action? Failed;
    void Append(byte[] pcm);
    void CompleteTurn();
    void Interrupt();
}

// Tracks real PCM ranges in the output clock, including gaps padded with silence.
// The audio callback only reads. Notifications are polled on a background worker.
internal sealed class LivePlaybackBuffer(Func<long> nextId) : IWaveProvider
{
    private sealed class Turn(long id)
    {
        public long Id = id, First = -1, End;
        public int Pending;
        public bool Complete, Started;
    }
    private sealed class Chunk(Turn turn, byte[] pcm)
    { public Turn Turn = turn; public byte[] Pcm = pcm; public int Offset; }
    private readonly object gate = new();
    private readonly Queue<Chunk> chunks = new();
    private readonly List<Turn> turns = [];
    private Turn? current;
    private long submitted;
    private int buffered;
    public WaveFormat WaveFormat { get; } = new(24000, 16, 1);
    public int BufferedBytes { get { lock (gate) return buffered; } }
    public bool IsSpeaking { get { lock (gate) return turns.Any(t => t.Started); } }
    public void Append(byte[] pcm)
    {
        lock (gate)
        {
            if (pcm.Length > LiveAudioPlayback.MaximumBufferedBytes - buffered)
                throw new LiveSessionException("Live output exceeded the thirty-second audio buffer. The session was stopped.");
            if (turns.Count >= 1024 || chunks.Count >= 4096)
                throw new LiveSessionException("Live output has too many buffered parts or turns. The session was stopped.");
            if (current == null) { current = new(nextId()); turns.Add(current); }
            chunks.Enqueue(new(current, pcm)); current.Pending += pcm.Length; buffered += pcm.Length;
        }
    }
    public void CompleteTurn() { lock (gate) { if (current != null) current.Complete = true; current = null; } }
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (gate)
        {
            int written = 0;
            while (written < count && chunks.TryPeek(out var chunk))
            {
                int length = Math.Min(count - written, chunk.Pcm.Length - chunk.Offset);
                chunk.Pcm.AsSpan(chunk.Offset, length).CopyTo(buffer.AsSpan(offset + written));
                if (chunk.Turn.First < 0) chunk.Turn.First = submitted + written;
                chunk.Turn.End = submitted + written + length;
                chunk.Turn.Pending -= length; buffered -= length;
                written += length; chunk.Offset += length;
                if (chunk.Offset == chunk.Pcm.Length) chunks.Dequeue();
            }
            buffer.AsSpan(offset + written, count - written).Clear(); submitted += count;
            return count;
        }
    }
    public LivePlaybackTurn[] Poll(long played)
    {
        lock (gate)
        {
            var changes = new List<LivePlaybackTurn>();
            foreach (var turn in turns)
            {
                if (!turn.Started && turn.First >= 0 && played > turn.First)
                { turn.Started = true; changes.Add(new(turn.Id, true)); }
                if (turn.Started && turn.Complete && turn.Pending == 0 && played >= turn.End)
                    changes.Add(new(turn.Id, false));
            }
            turns.RemoveAll(t => t.Started && t.Complete && t.Pending == 0 && played >= t.End);
            return changes.ToArray();
        }
    }
}

public sealed class LiveAudioPlayback(bool muted = false, PlaybackActivityHub? activity = null) : ILiveAudioPlayback
{
    public const int MaximumBufferedBytes = 24000 * 2 * 30;
    private readonly object gate = new();
    private LivePlaybackBuffer? buffer;
    private WaveOutEvent? output;
    private PlaybackActivityMonitor? monitor;
    private CancellationTokenSource? polling;
    private long turnId;
    private bool disposed;
    public int BufferedBytes { get { lock (gate) return buffer?.BufferedBytes ?? 0; } }
    public bool IsSpeaking { get { lock (gate) return buffer?.IsSpeaking == true; } }
    public event Action<LivePlaybackTurn>? TurnChanged;

    public void Append(byte[] pcm)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pcm.Length == 0 || pcm.Length % 2 != 0) throw new LiveSessionException("Live returned an incomplete PCM sample.");
            if (muted) return;
            if (!OperatingSystem.IsWindows()) throw new LiveSessionException("Live playback supports Windows only in this POC.");
            if (output == null)
            {
                buffer = new(() => ++turnId);
                output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3 };
                output.PlaybackStopped += (_, args) => { if (args.Exception != null) Failed?.Invoke(); };
                var meter = new PlaybackMeter(buffer);
                output.Init(meter); output.Play();
                if (activity != null) monitor = new PlaybackActivityMonitor(activity, meter, output.GetPosition);
                polling = new(); var token = polling.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
                        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                            lock (gate)
                            {
                                if (token.IsCancellationRequested || output == null || buffer == null) return;
                                foreach (var change in buffer.Poll(output.GetPosition())) TurnChanged?.Invoke(change);
                            }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception)
                    {
                        lock (gate)
                        {
                            try { Interrupt(); } catch (Exception) { }
                            Failed?.Invoke();
                        }
                    }
                });
            }
            buffer!.Append(pcm);
        }
    }
    public event Action? Failed;
    public void CompleteTurn() { lock (gate) buffer?.CompleteTurn(); }
    public void Interrupt()
    {
        lock (gate)
        {
            polling?.Cancel(); polling?.Dispose(); polling = null;
            monitor?.Dispose(); monitor = null;
            var previousOutput = output; output = null; buffer = null;
            try { previousOutput?.Stop(); }
            finally
            {
                try { previousOutput?.Dispose(); }
                finally { TurnChanged?.Invoke(new(++turnId, false)); }
            }
        }
    }
    public void Dispose() { lock (gate) { try { Interrupt(); } finally { disposed = true; } } }
}
