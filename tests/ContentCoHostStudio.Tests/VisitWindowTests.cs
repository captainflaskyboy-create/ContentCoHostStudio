using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class VisitWindowTests
{
    [AvaloniaFact]
    public async Task PlaybackCleanupFailureStillDisposesTransportAndPausesWithASanitizedError()
    {
        var clock = new ManualVisitClock(); var transport = new FakeLiveTransport();
        var window = new MainWindow(new HttpClient(), liveFactory: () => new(transport,
            new SilentLivePlayback { FailOnDispose = true }, new FakeLiveMicrophone(), clock: clock),
            audioDevices: new FakeAudioCatalog(), spoutOutput: new FakeSpoutOutput(), visitClock: clock);
        window.Show();
        try
        {
            Text(window, "ApiKeyBox").Text = "key"; window.FindControl<CheckBox>("UseLiveBox")!.IsChecked = true;
            Click(window, "StartVisitsButton"); Click(window, "VisitNowButton"); await UntilAsync(() => transport.Sent.Count == 1);
            clock.Advance(TimeSpan.FromSeconds(30));
            await UntilAsync(() => Button(window, "StartVisitsButton").Content?.ToString()?.StartsWith("Resume") == true);
            Assert.True(transport.Disposed); Assert.True(TextBlock(window, "LiveError").IsVisible);
            Assert.DoesNotContain("private-device-error", TextBlock(window, "LiveError").Text);
        }
        finally { window.Close(); await UntilAsync(() => !window.IsVisible); }
    }

    [AvaloniaFact]
    public async Task ClosingDuringAWaitOrAnActiveVisitDisarmsAndRejectsAllFutureTicks()
    {
        foreach (bool active in new[] { false, true })
        {
            var clock = new ManualVisitClock(); var transport = new FakeLiveTransport(); var mic = new FakeLiveMicrophone();
            var output = new FakeSpoutOutput(); int connections = 0;
            var window = new MainWindow(new HttpClient(), liveFactory: () =>
            { connections++; return new(transport, new SilentLivePlayback(), mic, clock: clock); },
                audioDevices: new FakeAudioCatalog(), spoutOutput: output, visitClock: clock);
            window.Show(); Text(window, "ApiKeyBox").Text = "key";
            window.FindControl<CheckBox>("UseLiveBox")!.IsChecked = true; Click(window, "StartVisitsButton");
            if (active)
            {
                Click(window, "VisitNowButton"); await UntilAsync(() => connections == 1);
                transport.Push("{\"setupComplete\":{}}"); await UntilAsync(() => mic.Subscriptions == 1);
            }
            window.Close(); await UntilAsync(() => output.Disposed && !window.IsVisible);
            clock.Advance(TimeSpan.FromDays(2)); Dispatcher.UIThread.RunJobs();
            Assert.Equal(active ? 1 : 0, connections);
            if (active) { Assert.True(transport.Disposed); Assert.Equal(1, mic.Releases); }
        }
    }

    [AvaloniaFact]
    public async Task AutomaticVisitsSnapshotSettingsAppearOnPlayedAudioAndDismissWithoutDisarming()
    {
        var clock = new ManualVisitClock();
        var transports = new List<FakeLiveTransport>(); var players = new List<SilentLivePlayback>(); var microphones = new List<FakeLiveMicrophone>();
        var output = new FakeSpoutOutput();
        var window = new MainWindow(new HttpClient(), liveFactory: () =>
        {
            var transport = new FakeLiveTransport(); var player = new SilentLivePlayback(); var mic = new FakeLiveMicrophone();
            transports.Add(transport); players.Add(player); microphones.Add(mic);
            return new(transport, player, mic, clock: clock);
        }, audioDevices: new FakeAudioCatalog(), spoutOutput: output, visitClock: clock);
        window.Show();
        try
        {
            Assert.False(window.FindControl<CheckBox>("UseLiveBox")!.IsChecked);
            Assert.Equal(30, Number(window, "LiveDurationValue").Value);
            Assert.Equal(5, Number(window, "VisitIntervalValue").Value);
            Assert.Equal(1, Combo(window, "LiveReplyLengthBox").SelectedIndex);
            Text(window, "ApiKeyBox").Text = "key";
            window.FindControl<CheckBox>("UseLiveBox")!.IsChecked = true;
            Click(window, "DesktopAvatarButton"); var original = window.DesktopAvatar!;
            original.Position = new(120, 180); original.ResizeAvatar(1.2);
            window.FindControl<CheckBox>("SpoutBox")!.IsChecked = true;
            await UntilAsync(() => output.Running);
            Click(window, "StartVisitsButton");
            Assert.False(original.IsVisible); Assert.False(Button(window, "StartLiveButton").IsEnabled);
            Assert.Empty(transports); Assert.False(output.Latest!.Visible); Assert.True(output.Running);
            Combo(window, "LiveReplyLengthBox").SelectedIndex = 0;
            Number(window, "LiveDurationValue").Value = 60;
            Click(window, "VisitNowButton"); await UntilAsync(() => transports.Count == 1 && transports[0].Sent.Count == 1);
            Assert.Contains("exactly one spoken word", transports[0].Sent.Single());
            Assert.False(Number(window, "LiveDurationValue").IsEnabled);
            transports[0].Push("{\"setupComplete\":{}}"); await UntilAsync(() => microphones[0].Subscriptions == 1);
            transports[0].Push(LiveSessionTests.AudioEvent("Word")); await UntilAsync(() => players[0].Received.Count == 2);
            Assert.False(original.IsVisible); // Receiving or queueing audio does not reveal it.
            players[0].StartPlayback(); await UntilAsync(() => original.IsVisible);
            Assert.True(output.Latest!.Visible); Assert.Equal(504, original.Width);
            Assert.Equal(new Avalonia.PixelPoint(120, 180), original.Position);
            players[0].Drain(); await UntilAsync(() => !original.IsVisible); Assert.False(output.Latest!.Visible);
            transports[0].Push(LiveSessionTests.AudioEvent("Again")); await UntilAsync(() => players[0].Received.Count == 4);
            players[0].StartPlayback(); await UntilAsync(() => original.IsVisible);
            // Click the actual popout control, rather than invoking schedule internals.
            var grid = Assert.IsType<Grid>(original.Content);
            var dismiss = Assert.IsType<Button>(grid.Children[0]); Assert.Equal("×", dismiss.Content);
            dismiss.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await UntilAsync(() => transports[0].Disposed && Button(window, "VisitNowButton").IsEnabled);
            Assert.False(original.IsVisible); Assert.False(output.Latest!.Visible); Assert.Equal(1, microphones[0].Releases);
            players[0].StartPlayback(); Dispatcher.UIThread.RunJobs(); Assert.False(original.IsVisible);
            Assert.Contains("Waiting", window.FindControl<TextBlock>("VisitStatus")!.Text);
            Assert.Single(transports);
            clock.Advance(TimeSpan.FromSeconds(299)); Dispatcher.UIThread.RunJobs(); Assert.Single(transports);
            clock.Advance(TimeSpan.FromSeconds(1)); await UntilAsync(() => transports.Count == 2);
            Assert.NotSame(transports[0], transports[1]);
            Click(window, "StopVisitsButton"); await UntilAsync(() => transports[1].Disposed && Button(window, "StartLiveButton").IsEnabled);
            Assert.True(original.IsVisible); Assert.False(original.AutomaticPresentation); Assert.True(output.Running);
        }
        finally { window.Close(); await UntilAsync(() => output.Disposed); }
    }

    [AvaloniaFact]
    public async Task FailurePausesModeChangesDisarmAndSixtySecondManualDeadlineUsesSelectedDuration()
    {
        var clock = new ManualVisitClock(); var transports = new List<FakeLiveTransport>();
        var window = new MainWindow(new HttpClient(), liveFactory: () =>
        {
            var t = new FakeLiveTransport(); transports.Add(t);
            return new(t, new SilentLivePlayback(), new FakeLiveMicrophone(), clock: clock);
        }, audioDevices: new FakeAudioCatalog(), spoutOutput: new FakeSpoutOutput(), visitClock: clock);
        window.Show();
        try
        {
            Text(window, "ApiKeyBox").Text = "key"; window.FindControl<CheckBox>("UseLiveBox")!.IsChecked = true;
            Click(window, "StartVisitsButton"); Click(window, "VisitNowButton"); await UntilAsync(() => transports.Count == 1);
            transports[0].Disconnect(); await UntilAsync(() => Button(window, "StartVisitsButton").Content?.ToString()?.StartsWith("Resume") == true);
            Assert.True(TextBlock(window, "LiveError").IsVisible);
            clock.Advance(TimeSpan.FromHours(1)); Dispatcher.UIThread.RunJobs(); Assert.Single(transports);
            Click(window, "StartVisitsButton"); window.FindControl<CheckBox>("LiveMicrophoneBox")!.IsChecked = false;
            await UntilAsync(() => !Button(window, "StopVisitsButton").IsEnabled);
            clock.Advance(TimeSpan.FromHours(1)); Dispatcher.UIThread.RunJobs(); Assert.Single(transports);
            Text(window, "TranscriptBox").Text = "Manual context";
            Combo(window, "LiveDurationUnit").SelectedIndex = 1; Number(window, "LiveDurationValue").Value = 1;
            Click(window, "StartLiveButton"); await UntilAsync(() => transports.Count == 2 && transports[1].Sent.Count == 1);
            transports[1].Push("{\"setupComplete\":{}}"); await UntilAsync(() => transports[1].Sent.Count == 2);
            clock.Advance(TimeSpan.FromSeconds(59)); Dispatcher.UIThread.RunJobs(); Assert.False(transports[1].Disposed);
            clock.Advance(TimeSpan.FromSeconds(1)); await UntilAsync(() => transports[1].Disposed);
            Assert.Contains("TimedOut", TextBlock(window, "LiveStatus").Text);
        }
        finally { window.Close(); await UntilAsync(() => !window.IsVisible); }
    }

    [Fact]
    public void DismissButtonIsInTheNativeHitRegionAtMultipleMonitorScales()
    {
        var mask = new StreamerHitRegion.AlphaMask(3, 3, [0, 0, 0, 0, 255, 0, 0, 0, 0]);
        foreach (double scale in new[] { 1d, 1.5, 2d })
        {
            var regions = StreamerHitRegion.BuildRectangles(mask, 420, 420, scale, 1, new("sprite", 0, 0, false, 0, 1), chromeHeight: 32);
            Assert.Equal(StreamerHitRegion.DismissRectangle(420, 32, scale), regions[0]);
            Assert.All(regions.Skip(1), rect => Assert.True(rect.Top >= 32 * scale));
            Assert.True(regions[0].Left > 0); Assert.True(regions[0].Right < 420 * scale);
        }
    }

    private static NumericUpDown Number(MainWindow w, string n) => w.FindControl<NumericUpDown>(n)!;
    private static ComboBox Combo(MainWindow w, string n) => w.FindControl<ComboBox>(n)!;
    private static TextBox Text(MainWindow w, string n) => w.FindControl<TextBox>(n)!;
    private static TextBlock TextBlock(MainWindow w, string n) => w.FindControl<TextBlock>(n)!;
    private static Button Button(MainWindow w, string n) => w.FindControl<Button>(n)!;
    private static void Click(MainWindow w, string n) => Button(w, n).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) { Dispatcher.UIThread.RunJobs(); if (condition()) return; await Task.Delay(5, limit.Token); }
    }
}
