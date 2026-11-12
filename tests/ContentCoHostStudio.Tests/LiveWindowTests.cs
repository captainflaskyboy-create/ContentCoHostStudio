using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class LiveWindowTests
{
    [AvaloniaFact]
    public async Task StandardDefaultsAndLiveTranscriptFlowKeepTextSeparateAndFreezeSettings()
    {
        var transport = new FakeLiveTransport();
        var player = new SilentLivePlayback();
        var mic = new FakeLiveMicrophone();
        var window = CreateWindow(() => new GeminiLiveSession(transport, player, mic));
        window.Show();
        Assert.False(Box(window, "UseLiveBox").IsChecked);
        Assert.True(Box(window, "LiveMicrophoneBox").IsChecked);
        Assert.Equal("Puck", window.FindControl<ComboBox>("VoiceBox")!.SelectedItem);
        Assert.Equal(GeminiSpeechService.DroidStyle, Text(window, "VoiceStyleBox").Text);
        Text(window, "ApiKeyBox").Text = "fake-key";
        Text(window, "TranscriptBox").Text = "Original human text";
        Box(window, "UseLiveBox").IsChecked = true;
        Box(window, "LiveMicrophoneBox").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(Button(window, "GenerateButton").IsEnabled);
        Click(window, "StartLiveButton");
        transport.Push("{\"setupComplete\":{}}");
        await UntilAsync(() => transport.Sent.Count == 2);
        Assert.False(Text(window, "PersonalityBox").IsEnabled);
        Assert.False(Text(window, "VoiceStyleBox").IsEnabled);
        Assert.True(Button(window, "ListenButton").IsEnabled);
        Assert.Contains("never uploads", window.FindControl<TextBlock>("LiveInputNotice")!.Text);
        transport.Push(LiveSessionTests.AudioEvent("Robot reply"));
        await UntilAsync(() => Text(window, "ResponseBox").Text?.Contains("Robot reply") == true);
        Assert.Equal("Original human text", Text(window, "TranscriptBox").Text);
        Assert.Contains("gemini-3.8-live", window.FindControl<TextBlock>("ResponseHeading")!.Text);
        Assert.Equal(0, mic.Subscriptions);
        player.Drain();
        // The regular UI timer also enables this after the output buffer drains.
        Text(window, "TranscriptBox").Text = "Second human snapshot";
        Dispatcher.UIThread.RunJobs();
        Assert.True(Button(window, "SendTranscriptButton").IsEnabled);
        Click(window, "SendTranscriptButton");
        await UntilAsync(() => transport.Sent.Count == 3);
        Assert.Contains("Second human snapshot", transport.Sent.Last());
        Box(window, "UseLiveBox").IsChecked = false;
        await UntilAsync(() => transport.Disposed);
        Assert.True(Text(window, "PersonalityBox").IsEnabled);
        Assert.True(Button(window, "GenerateButton").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task MicrophoneOwnershipModeSwitchAndStaleEventsCannotOverwriteNewSession()
    {
        var transports = new[] { new FakeLiveTransport(), new FakeLiveTransport() };
        var microphones = new[] { new FakeLiveMicrophone(), new FakeLiveMicrophone() };
        int index = 0;
        var window = CreateWindow(() =>
        {
            int current = index++;
            return new GeminiLiveSession(transports[current], new SilentLivePlayback(), microphones[current]);
        });
        window.Show();
        Text(window, "ApiKeyBox").Text = "fake-key";
        Box(window, "UseLiveBox").IsChecked = true;
        Click(window, "StartLiveButton");
        transports[0].Push("{\"setupComplete\":{}}");
        await UntilAsync(() => microphones[0].Subscriptions == 1);
        Assert.False(Button(window, "ListenButton").IsEnabled);
        Assert.Contains("STREAMING TO GOOGLE", window.FindControl<TextBlock>("LiveInputNotice")!.Text);
        Box(window, "LiveMicrophoneBox").IsChecked = false;
        await UntilAsync(() => transports[0].Disposed);
        Assert.Equal(1, microphones[0].Releases);
        Assert.True(Button(window, "ListenButton").IsEnabled);
        Assert.Equal(1, index); // Switching did not reconnect.
        Text(window, "TranscriptBox").Text = "Fresh input";
        Click(window, "StartLiveButton");
        transports[1].Push("{\"setupComplete\":{}}");
        await UntilAsync(() => transports[1].Sent.Count == 2);
        transports[0].Push(LiveSessionTests.AudioEvent("Stale reply"));
        transports[1].Push(LiveSessionTests.AudioEvent("Fresh reply"));
        await UntilAsync(() => Text(window, "ResponseBox").Text?.Contains("Fresh reply") == true);
        Assert.DoesNotContain("Stale", Text(window, "ResponseBox").Text);
        window.Close();
        await UntilAsync(() => transports[1].Disposed && !window.IsVisible);
        Assert.Equal("", Text(window, "ApiKeyBox").Text);
    }

    [AvaloniaFact]
    public async Task ExpiryRestoresControlsAndRequiresExplicitRestart()
    {
        var transport = new FakeLiveTransport();
        var mic = new FakeLiveMicrophone();
        var window = CreateWindow(() => new GeminiLiveSession(transport, new SilentLivePlayback(), mic, TimeSpan.FromMilliseconds(150)));
        window.Show();
        Text(window, "ApiKeyBox").Text = "fake-key";
        Box(window, "UseLiveBox").IsChecked = true;
        Click(window, "StartLiveButton");
        transport.Push("{\"setupComplete\":{}}");
        await UntilAsync(() => transport.Disposed);
        Assert.Contains("TimedOut", window.FindControl<TextBlock>("LiveStatus")!.Text);
        Assert.True(Button(window, "ListenButton").IsEnabled);
        Assert.True(Button(window, "StartLiveButton").IsEnabled);
        Assert.Equal(1, transport.ConnectCount);
        window.Close();
    }

    private static MainWindow CreateWindow(Func<GeminiLiveSession> factory) => new(new HttpClient(new SpeechHttpHandler((_, _) =>
        throw new Xunit.Sdk.XunitException("Live must not call Standard text or TTS HTTP services."))), liveFactory: factory);
    private static CheckBox Box(MainWindow window, string name) => window.FindControl<CheckBox>(name)!;
    private static TextBox Text(MainWindow window, string name) => window.FindControl<TextBox>(name)!;
    private static Button Button(MainWindow window, string name) => window.FindControl<Button>(name)!;
    private static void Click(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.True(Button(window, name).IsEnabled);
        Button(window, name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) break;
            await Task.Delay(5, timeout.Token);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
