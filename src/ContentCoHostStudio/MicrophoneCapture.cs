using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ContentCoHostStudio;

public sealed record AudioPacket(byte[] Bytes, long CapturedAt)
{
    public AudioPacket(byte[] bytes) : this(bytes, Stopwatch.GetTimestamp()) { }
}

public sealed class AudioBufferOverflowException() : Exception("The audio input buffer filled. Capture stopped rather than silently dropping speech.");

// A byte budget stays meaningful even when device callback sizes change.
public sealed class BoundedAudioQueue(int capacityBytes)
{
    private readonly Channel<AudioPacket> channel = Channel.CreateUnbounded<AudioPacket>();
    private int bufferedBytes;
    private Exception? failure;
    public int BufferedBytes => Volatile.Read(ref bufferedBytes);
    public bool TryWrite(byte[] bytes) => TryWrite(new AudioPacket(bytes));
    public bool TryWrite(AudioPacket packet)
    {
        byte[] bytes = packet.Bytes;
        if (Interlocked.Add(ref bufferedBytes, bytes.Length) > capacityBytes)
        {
            Interlocked.Add(ref bufferedBytes, -bytes.Length);
            return false;
        }
        if (channel.Writer.TryWrite(packet))
            return true;
        Interlocked.Add(ref bufferedBytes, -bytes.Length);
        return false;
    }
    public void Complete(Exception? error = null)
    {
        if (error != null)
            Interlocked.CompareExchange(ref failure, error, null);
        channel.Writer.TryComplete(error);
    }
    public async IAsyncEnumerable<byte[]> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var packet in ReadPacketsAsync(cancellationToken).ConfigureAwait(false))
            yield return packet.Bytes;
    }
    public async IAsyncEnumerable<AudioPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref failure) is { } error)
                throw error;
            if (channel.Reader.TryRead(out var packet))
            {
                Interlocked.Add(ref bufferedBytes, -packet.Bytes.Length);
                yield return packet;
            }
        }
    }
}

public interface IMicrophoneSource
{
    Task<MicrophoneSubscription> SubscribeAsync(CancellationToken cancellationToken);
}

public sealed class MicrophoneSubscription(BoundedAudioQueue audio, Func<Task> release) : IAsyncDisposable
{
    private int released;
    public BoundedAudioQueue Audio { get; } = audio;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
            await release().ConfigureAwait(false);
    }
}

public interface IMicrophoneCaptureDevice
{
    Task CaptureAsync(Action<AudioPacket> publishPcm, CancellationToken cancellationToken);
}

public sealed class MicrophoneCaptureService(IMicrophoneCaptureDevice? device = null) : IMicrophoneSource
{
    private readonly IMicrophoneCaptureDevice device = device ?? new WindowsMicrophoneCapture();
    private readonly SemaphoreSlim lifetimeGate = new(1, 1);
    private readonly Dictionary<int, BoundedAudioQueue> subscribers = [];
    private readonly object subscribersLock = new();
    private int nextId;
    private Task captureTask = Task.CompletedTask;
    private CancellationTokenSource? captureSource;

    public async Task<MicrophoneSubscription> SubscribeAsync(CancellationToken cancellationToken)
    {
        await lifetimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool start;
            lock (subscribersLock)
                start = subscribers.Count == 0;
            if (start)
                await captureTask.ConfigureAwait(false);
            var queue = new BoundedAudioQueue(16000 * 2 * 2);
            int id = ++nextId;
            lock (subscribersLock)
            {
                subscribers.Add(id, queue);
            }
            if (start)
            {
                captureSource?.Dispose();
                captureSource = new CancellationTokenSource();
                CancellationToken token = captureSource.Token;
                captureTask = Task.Run(() => RunDeviceAsync(token));
            }
            return new MicrophoneSubscription(queue, () => ReleaseAsync(id, queue));
        }
        finally { lifetimeGate.Release(); }
    }

    private void Publish(AudioPacket packet)
    {
        if (packet.Bytes.Length == 0)
            return;
        lock (subscribersLock)
        {
            foreach (int id in subscribers.Keys.ToArray())
            {
                if (subscribers[id].TryWrite(packet))
                    continue;
                subscribers[id].Complete(new InvalidOperationException("Audio input processing fell behind. The two-second audio buffer filled; start listening again."));
                subscribers.Remove(id);
            }
            if (subscribers.Count == 0)
                captureSource?.Cancel();
        }
    }

    private async Task RunDeviceAsync(CancellationToken token)
    {
        Exception? failure = null;
        try { await device.CaptureAsync(Publish, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            failure = new InvalidOperationException(exception is AudioBufferOverflowException ? exception.Message :
                "Could not capture the selected audio device. Check its connection, Windows audio settings, microphone permissions, and audio format. Refresh devices and select again if it was disconnected.");
        }
        finally
        {
            lock (subscribersLock)
            {
                foreach (var queue in subscribers.Values)
                    queue.Complete(failure);
                subscribers.Clear();
            }
        }
    }

    private async Task ReleaseAsync(int id, BoundedAudioQueue queue)
    {
        await lifetimeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool last;
            lock (subscribersLock)
            {
                last = subscribers.Count == 1 && subscribers.ContainsKey(id);
                if (!last)
                    subscribers.Remove(id);
            }
            if (last)
            {
                // Keep the last queue attached while the resampler's tail is flushed.
                captureSource?.Cancel();
                await captureTask.ConfigureAwait(false);
            }
            queue.Complete();
        }
        finally { lifetimeGate.Release(); }
    }
}

public sealed class WindowsMicrophoneCapture(AudioInputSettings? settings = null) : IMicrophoneCaptureDevice
{
    public async Task CaptureAsync(Action<AudioPacket> publishPcm, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        using var devices = new MMDeviceEnumerator();
        var selection = settings?.Current ?? new AudioInputSelection();
        bool loopback = selection.Kind == AudioInputKind.ComputerAudio;
        // Explicit endpoints must fail if unavailable, never silently switch devices.
        using var device = selection.DeviceId is { } id ? devices.GetDevice(id) :
            devices.GetDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture, loopback ? Role.Multimedia : Role.Console);
        if (device.DataFlow != (loopback ? DataFlow.Render : DataFlow.Capture) || device.State != DeviceState.Active)
            throw new InvalidOperationException("The selected audio device is unavailable.");
        using WasapiCapture capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        var raw = new BoundedAudioQueue(capture.WaveFormat.AverageBytesPerSecond * 2);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var converter = new AudioConverter(capture.WaveFormat);
        int overflow = 0;
        Exception? captureError = null;
        capture.DataAvailable += (_, args) =>
        {
            if (!raw.TryWrite(args.Buffer.AsSpan(0, args.BytesRecorded).ToArray()))
                Interlocked.Exchange(ref overflow, 1);
        };
        capture.RecordingStopped += (_, args) =>
        {
            captureError = args.Exception;
            raw.Complete();
            stopped.TrySetResult();
        };
        capture.StartRecording();
        long lastCapture = Stopwatch.GetTimestamp();
        try
        {
            await foreach (var packet in raw.ReadPacketsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Volatile.Read(ref overflow) != 0)
                    throw new AudioBufferOverflowException();
                lastCapture = packet.CapturedAt;
                publishPcm(new AudioPacket(converter.Convert(packet.Bytes), packet.CapturedAt));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            capture.StopRecording();
            await stopped.Task.ConfigureAwait(false);
        }
        if (Volatile.Read(ref overflow) != 0)
            throw new AudioBufferOverflowException();
        if (captureError != null)
            throw new InvalidOperationException("Audio input capture stopped or fell behind.");
        await foreach (var packet in raw.ReadPacketsAsync(CancellationToken.None).ConfigureAwait(false))
        {
            lastCapture = packet.CapturedAt;
            publishPcm(new AudioPacket(converter.Convert(packet.Bytes), packet.CapturedAt));
        }
        publishPcm(new AudioPacket(converter.Flush(), lastCapture));
    }
}
