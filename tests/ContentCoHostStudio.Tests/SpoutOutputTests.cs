using System.Collections.Concurrent;
using System.Runtime.InteropServices;

using Xunit;

namespace ContentCoHostStudio.Tests;

public sealed class SpoutOutputTests
{
    private static string[] Sprites => typeof(SpriteImages).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("Sprites.", StringComparison.Ordinal) && n.EndsWith(".png", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task SenderIsOptInAndAllGraphicsWorkStaysOnOneThread()
    {
        var ids = new ConcurrentBag<int>(); var renderer = new FakeRenderer(ids); var sender = new FakeSender(ids);
        await using var output = new SpoutOutputService([], () => { ids.Add(Environment.CurrentManagedThreadId); return sender; }, () => renderer, true);
        Assert.False(output.Running); Assert.Empty(ids);
        var frame = new AvatarOutputSnapshot("frame", 7, -4, true, 90, 100);
        output.Publish(frame); await output.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(output.Running); Assert.Equal(frame, renderer.Last);
        Assert.Equal("Test sender", output.SenderName);
        await output.StopAsync(); Assert.False(output.Running);
        Assert.True(sender.Disposed); Assert.True(renderer.Disposed); Assert.Single(ids.Distinct());
    }

    [Fact]
    public async Task StartFailureAllowsRetryAndUnavailablePlatformsNeverLoadNativeCode()
    {
        var attempts = 0; var ids = new ConcurrentBag<int>();
        await using var output = new SpoutOutputService([], () =>
        { if (++attempts == 1) throw new IOException("Sender library missing"); return new FakeSender(ids); }, () => new FakeRenderer(ids), true);
        await Assert.ThrowsAsync<IOException>(() => output.StartAsync(TestContext.Current.CancellationToken));
        Assert.False(output.Running); Assert.Contains("missing", output.Status);
        await output.StartAsync(TestContext.Current.CancellationToken); Assert.True(output.Running);
        await output.StopAsync(); await output.StartAsync(TestContext.Current.CancellationToken); Assert.True(output.Running);
        await using var unsupported = new SpoutOutputService([], () => throw new Exception("Must never load"), available: false);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => unsupported.StartAsync(TestContext.Current.CancellationToken));
        Assert.False(unsupported.Running);
    }

    [Fact]
    public async Task MidStreamDeviceFailureStopsSenderAndAllowsCleanShutdown()
    {
        var ids = new ConcurrentBag<int>(); var sender = new FakeSender(ids);
        await using var output = new SpoutOutputService([], () => sender, () => new FakeRenderer(ids), true);
        await output.StartAsync(TestContext.Current.CancellationToken);
        sender.Fail = true;
        await LiveTestWait.UntilAsync(() => !output.Running);
        Assert.Contains("failed", output.Status);
        await output.StopAsync();
        Assert.True(sender.Disposed);
    }

    [Fact]
    public void OffscreenFramesPreserveTransparencyFlipAndRotation()
    {
        using var renderer = new SpoutFrameRenderer(Sprites);
        var frame = new AvatarOutputSnapshot("Sprites.normal_idle001.png", 0, 0, false, 0, 1);
        byte[] Read(AvatarOutputSnapshot snapshot)
        { var bytes = new byte[512 * 512 * 4]; Marshal.Copy(renderer.Render(snapshot), bytes, 0, bytes.Length); return bytes; }
        var normal = Read(frame); var flipped = Read(frame with { Flip = true }); var rotated = Read(frame with { Rotation = 90 });
        Assert.Equal(0, normal[3]); Assert.Contains((byte)255, normal.Where((_, i) => i % 4 == 3));
        Assert.False(normal.SequenceEqual(flipped)); Assert.False(normal.SequenceEqual(rotated));
        for (var y = 0; y < 512; y++)
            for (var x = 0; x < 512; x++)
                Assert.Equal(normal[(y * 512 + x) * 4 + 3], flipped[(y * 512 + 511 - x) * 4 + 3]);
    }

    [Fact]
    public async Task RunningSenderAcceptsPackRevisionChanges()
    {
        var starter = new AvatarPackLibrary(Path.Combine(Path.GetTempPath(), "sas-spout-pack-" + Guid.NewGuid().ToString("N"))).Starter;
        var next = starter with { Revision = "next" };
        var ids = new ConcurrentBag<int>();
        await using var output = new SpoutOutputService(starter, () => new FakeSender(ids), true);
        output.Publish(new("Sprites.normal_idle001.png", 0, 0, false, 0, 1, starter.Revision));
        await output.StartAsync(TestContext.Current.CancellationToken);
        output.SetPack(next);
        output.Publish(new("Sprites.normal_idle001.png", 0, 0, false, 0, 2, next.Revision));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(output.Running);
        await output.StopAsync();
        Assert.NotEmpty(ids);
    }

    private sealed class FakeSender(ConcurrentBag<int> ids) : ISpoutSender
    {
        public string Name => "Test sender";
        public volatile bool Fail;
        public bool Disposed { get; private set; }
        public void Send(nint pixels) { ids.Add(Environment.CurrentManagedThreadId); if (Fail) throw new IOException("Device lost"); }
        public void Dispose() { ids.Add(Environment.CurrentManagedThreadId); Disposed = true; }
    }
    private sealed class FakeRenderer(ConcurrentBag<int> ids) : IAvatarFrameRenderer
    {
        public AvatarOutputSnapshot? Last;
        public bool Disposed { get; private set; }
        public nint Render(AvatarOutputSnapshot snapshot) { ids.Add(Environment.CurrentManagedThreadId); Last = snapshot; return 1; }
        public void Dispose() { ids.Add(Environment.CurrentManagedThreadId); Disposed = true; }
    }
}
