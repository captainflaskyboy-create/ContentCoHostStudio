using Xunit;

namespace ContentCoHostStudio.Tests;

public class SharedMicrophoneTests
{
    [Fact]
    public async Task SubscribersShareOneCaptureAndReleasingLivePreservesLocalSubscription()
    {
        var device = new FakeCaptureDevice();
        var microphone = new MicrophoneCaptureService(device);
        await using var local = await microphone.SubscribeAsync(TestContext.Current.CancellationToken);
        await using var live = await microphone.SubscribeAsync(TestContext.Current.CancellationToken);
        await LiveTestWait.UntilAsync(() => device.Starts == 1);
        var packet = new AudioPacket(new byte[] { 1, 2 }, 123);
        device.Publish!(packet);
        await using var localReader = local.Audio.ReadPacketsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var liveReader = live.Audio.ReadPacketsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await localReader.MoveNextAsync());
        Assert.True(await liveReader.MoveNextAsync());
        Assert.Equal(123, localReader.Current.CapturedAt);
        Assert.Same(localReader.Current, liveReader.Current);
        await live.DisposeAsync();
        Assert.Equal(0, device.Stops);
        device.Publish!(new AudioPacket(new byte[] { 3, 4 }, 456));
        Assert.True(await localReader.MoveNextAsync());
        Assert.Equal(456, localReader.Current.CapturedAt);
        await local.DisposeAsync();
        Assert.Equal(1, device.Stops);
        await using var restarted = await microphone.SubscribeAsync(TestContext.Current.CancellationToken);
        await LiveTestWait.UntilAsync(() => device.Starts == 2);
        Assert.Equal(1, device.MaximumConcurrent);
    }

    [Fact]
    public async Task FullSubscriberFailsImmediatelyWithoutStoppingHealthySubscriber()
    {
        var device = new FakeCaptureDevice();
        var microphone = new MicrophoneCaptureService(device);
        await using var slow = await microphone.SubscribeAsync(TestContext.Current.CancellationToken);
        await using var healthy = await microphone.SubscribeAsync(TestContext.Current.CancellationToken);
        await LiveTestWait.UntilAsync(() => device.Starts == 1);
        await using var reader = healthy.Audio.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        device.Publish!(new AudioPacket(new byte[64000]));
        Assert.True(await reader.MoveNextAsync());
        device.Publish!(new AudioPacket(new byte[] { 1, 2 }));
        Assert.True(await reader.MoveNextAsync());
        await using var slowReader = slow.Audio.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await slowReader.MoveNextAsync());
        Assert.Equal(0, device.Stops);
        Assert.Equal(2, reader.Current.Length);
        await slow.DisposeAsync();
        await healthy.DisposeAsync();
        Assert.Equal(1, device.Stops);
    }

    [Fact]
    public async Task ByteBudgetCancellationAndCompletionPreserveQueuedCaptureTime()
    {
        var queue = new BoundedAudioQueue(4);
        Assert.True(queue.TryWrite(new AudioPacket(new byte[4], 123)));
        Assert.False(queue.TryWrite(new byte[2]));
        Assert.Equal(4, queue.BufferedBytes);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using (var reader = queue.ReadPacketsAsync(cancelled.Token).GetAsyncEnumerator(cancelled.Token))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
        Assert.Equal(4, queue.BufferedBytes);
        queue.Complete();
        await using var drain = queue.ReadPacketsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await drain.MoveNextAsync());
        Assert.Equal(123, drain.Current.CapturedAt);
        Assert.Equal(0, queue.BufferedBytes);
        Assert.False(await drain.MoveNextAsync());
        Assert.False(queue.TryWrite(new byte[2]));
        Assert.Equal(0, queue.BufferedBytes);
    }
}

internal sealed class FakeCaptureDevice : IMicrophoneCaptureDevice
{
    public Action<AudioPacket>? Publish;
    public int Starts;
    public int Stops;
    public int MaximumConcurrent;
    private int concurrent;
    public async Task CaptureAsync(Action<AudioPacket> publishPcm, CancellationToken token)
    {
        Publish = publishPcm;
        MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref concurrent));
        Interlocked.Increment(ref Starts);
        try { await Task.Delay(Timeout.Infinite, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Interlocked.Decrement(ref concurrent); Interlocked.Increment(ref Stops); }
    }
}
