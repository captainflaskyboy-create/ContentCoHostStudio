using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace ContentCoHostStudio.Tests;

internal sealed class FakeLiveTransport : ILiveTransport
{
    private readonly Channel<(byte[] Bytes, bool End, WebSocketMessageType Type)> frames = Channel.CreateUnbounded<(byte[], bool, WebSocketMessageType)>();
    public ConcurrentQueue<string> Sent { get; } = new();
    public Func<CancellationToken, Task>? Connecting { get; set; }
    public Func<string, CancellationToken, Task>? Sending { get; set; }
    public Func<CancellationToken, Task>? Closing { get; set; }
    public bool Aborted { get; private set; }
    public bool Disposed { get; private set; }
    public int ConnectCount { get; private set; }
    public void Push(string json, bool fragmented = false)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        if (fragmented)
            frames.Writer.TryWrite((bytes[..(bytes.Length / 2)], false, WebSocketMessageType.Text));
        frames.Writer.TryWrite((fragmented ? bytes[(bytes.Length / 2)..] : bytes, true, WebSocketMessageType.Text));
    }
    public void Disconnect() => frames.Writer.TryWrite(([], true, WebSocketMessageType.Close));
    public async Task ConnectAsync(string key, CancellationToken token)
    {
        ConnectCount++;
        if (Connecting != null) await Connecting(token);
    }
    public async Task SendAsync(string json, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Sending != null) await Sending(json, token);
        token.ThrowIfCancellationRequested();
        Sent.Enqueue(json);
    }
    public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
    {
        var frame = await frames.Reader.ReadAsync(token);
        frame.Bytes.CopyTo(buffer);
        return new(frame.Bytes.Length, frame.Type, frame.End);
    }
    public Task CloseAsync(CancellationToken token) => Closing?.Invoke(token) ?? Task.CompletedTask;
    public void Abort() { Aborted = true; Disconnect(); }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

internal sealed class SilentLivePlayback : ILiveAudioPlayback
{
    private int buffered;
    public int BufferedBytes => Volatile.Read(ref buffered);
    public bool IsSpeaking { get; private set; }
    public event Action<LivePlaybackTurn>? TurnChanged;
    public event Action? Failed;
    public void Fail() => Failed?.Invoke();
    private long turn;
    public void StartPlayback() { IsSpeaking = true; TurnChanged?.Invoke(new(++turn, true)); }
    public void CompleteTurn() { }
    public ConcurrentQueue<byte[]> Received { get; } = new();
    public int Interrupts;
    public bool Disposed { get; private set; }
    public bool DrainImmediately { get; set; }
    public bool FailOnDispose { get; set; }
    public void Append(byte[] pcm)
    {
        if (BufferedBytes + pcm.Length > LiveAudioPlayback.MaximumBufferedBytes)
            throw new LiveSessionException("Live output exceeded the thirty-second audio buffer. The session was stopped.");
        Received.Enqueue(pcm);
        if (!DrainImmediately) Interlocked.Add(ref buffered, pcm.Length);
    }
    public void Drain() { Interlocked.Exchange(ref buffered, 0); IsSpeaking = false; TurnChanged?.Invoke(new(turn, false)); }
    public void Interrupt() { Interlocked.Increment(ref Interrupts); Drain(); }
    public void Dispose() { Disposed = true; Drain(); if (FailOnDispose) throw new IOException("private-device-error"); }
}

internal sealed class FakeLiveMicrophone : IMicrophoneSource
{
    public BoundedAudioQueue Queue { get; } = new(64000);
    public int Subscriptions;
    public int Releases;
    public Task<MicrophoneSubscription> SubscribeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Subscriptions);
        return Task.FromResult(new MicrophoneSubscription(Queue, () =>
        {
            Interlocked.Increment(ref Releases);
            Queue.Complete();
            return Task.CompletedTask;
        }));
    }
}

internal static class LiveTestWait
{
    public static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}
