using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class AvatarWindowTests
{
    [AvaloniaFact]
    public async Task AvatarDefaultsStayIdleAndPopoutSharesSnapshotsIndependentlyOfSpout()
    {
        var hub = new PlaybackActivityHub();
        var output = new FakeSpoutOutput();
        var window = new MainWindow(new HttpClient(), spoutOutput: output, playbackActivity: hub, audioDevices: new FakeAudioCatalog());
        window.Show();
        Assert.False(window.FindControl<CheckBox>("SpoutBox")!.IsChecked);
        Assert.False(window.FindControl<CheckBox>("AvatarBobBox")!.IsChecked);
        Assert.False(window.FindControl<CheckBox>("AvatarShakeBox")!.IsChecked);
        Assert.False(window.FindControl<Button>("RemoveAvatarButton")!.IsEnabled);
        var preview = window.FindControl<AvatarCanvas>("AvatarPreview")!;
        Assert.Contains("normal_idle", preview.Snapshot!.Sprite);
        Click(window, "DesktopAvatarButton");
        var desktop = Assert.IsType<StreamerWindow>(window.DesktopAvatar);
        Assert.Equal(420, desktop.Width);
        Assert.True(desktop.Topmost);
        long id = hub.Begin(); hub.Report(id, .5f);
        await UntilAsync(() => preview.Snapshot!.Sprite.Contains("talk"));
        Assert.Same(preview.Snapshot, desktop.Avatar.Snapshot);
        Assert.Same(preview.Snapshot, output.Latest);
        window.FindControl<CheckBox>("SpoutBox")!.IsChecked = true;
        await UntilAsync(() => output.Running);
        desktop.ResizeAvatar(1.2); Assert.Equal(504, desktop.Width);
        desktop.Close();
        Assert.Null(window.DesktopAvatar);
        Assert.True(output.Running);
        hub.Stop(id);
        await UntilAsync(() => preview.Snapshot!.Sprite.Contains("idle"));
        Click(window, "DesktopAvatarButton");
        Assert.NotSame(desktop, window.DesktopAvatar);
        Assert.Equal(420, window.DesktopAvatar!.Width);
        window.Close();
        await UntilAsync(() => output.Disposed && !window.IsVisible);
        Assert.Null(window.DesktopAvatar);
        Assert.False(hub.Current.Active);
    }

    [AvaloniaFact]
    public async Task UnavailableSpoutLeavesAvatarPreviewAndDesktopUsable()
    {
        var output = new FakeSpoutOutput { Available = false };
        var window = new MainWindow(new HttpClient(), spoutOutput: output, audioDevices: new FakeAudioCatalog());
        window.Show();
        Assert.False(window.FindControl<CheckBox>("SpoutBox")!.IsEnabled);
        Assert.NotNull(window.FindControl<AvatarCanvas>("AvatarPreview")!.Snapshot);
        Click(window, "DesktopAvatarButton");
        Assert.NotNull(window.DesktopAvatar);
        Assert.Equal(0, output.Starts);
        window.Close();
        await UntilAsync(() => output.Disposed);
    }

    [AvaloniaFact]
    public async Task DecodedSpriteCacheAndPopoutSizeStayBounded()
    {
        var pack = new AvatarPackLibrary().Starter;
        using var images = new SpriteImages(pack);
        foreach (string sprite in pack.Sprites.Keys) images.Get(sprite);
        Assert.InRange(images.CachedBytes, 1, 64 * 1024 * 1024);
        var window = new StreamerWindow(images);
        window.Show();
        window.ResizeAvatar(100); Assert.Equal(1800, window.Width);
        window.ResizeAvatar(.0001); Assert.Equal(120, window.Width);
        window.Close();
        await Task.CompletedTask;
    }

    [Fact]
    public void DesktopHitRegionOmitsTransparentPixelsAtDifferentDisplayScales()
    {
        var mask = new StreamerHitRegion.AlphaMask(3, 3, [0, 0, 0, 0, 255, 0, 0, 0, 0]);
        var frame = new AvatarOutputSnapshot("sprite", 0, 0, false, 0, 1);
        foreach (double scale in new[] { 1d, 1.5, 2d })
        {
            var rectangles = StreamerHitRegion.BuildRectangles(mask, 120, 120, scale, 1, frame);
            Assert.NotEmpty(rectangles);
            Assert.All(rectangles, rect => Assert.True(rect.Left > 0 && rect.Top > 0 && rect.Right < 120 * scale && rect.Bottom < 120 * scale));
        }
    }

    private static void Click(MainWindow window, string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10, timeout.Token);
        }
    }
}

internal sealed class FakeSpoutOutput : ISpoutOutput
{
    public bool Available { get; init; } = true;
    public bool Running { get; private set; }
    public string SenderName => "Fake sender";
    public string Status => Running ? "Sender running; receiver not monitored" : "Off";
    public event Action? Changed;
    public AvatarOutputSnapshot? Latest;
    public bool Disposed;
    public int Starts;
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Starts++; Running = true; Changed?.Invoke(); return Task.CompletedTask;
    }
    public Task StopAsync() { Running = false; Changed?.Invoke(); return Task.CompletedTask; }
    public void Publish(AvatarOutputSnapshot snapshot) => Latest = snapshot;
    public async ValueTask DisposeAsync() { await StopAsync(); Disposed = true; }
}
