using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class AudioInputWindowTests
{
    private static readonly AudioDeviceOption Mic = new("mic-2", "USB microphone", AudioInputKind.Microphone);
    private static readonly AudioDeviceOption Output = new("output-2", "Headphones", AudioInputKind.ComputerAudio);

    [AvaloniaFact]
    public async Task SeparateDeviceListsRouteSelectedSourceAndDefaultLoopbackMute()
    {
        var settings = new AudioInputSettings();
        var catalog = new FakeAudioCatalog { Devices = [Mic, Output, Mic] };
        var window = Create(catalog, settings);
        window.Show();
        await UntilAsync(() => Combo(window, "MicrophoneDeviceBox").ItemCount == 2);
        Assert.Equal(2, Combo(window, "ComputerAudioDeviceBox").ItemCount);
        Assert.Equal(AudioInputKind.Microphone, settings.Current.Kind);
        Combo(window, "MicrophoneDeviceBox").SelectedIndex = 1;
        Assert.Equal(Mic.Id, settings.Current.DeviceId);
        Combo(window, "AudioSourceBox").SelectedIndex = 1;
        Combo(window, "ComputerAudioDeviceBox").SelectedIndex = 1;
        Assert.Equal(new AudioInputSelection(AudioInputKind.ComputerAudio, Output.Id), settings.Current);
        Assert.True(window.FindControl<TextBlock>("LoopbackNote")!.IsVisible);
        Assert.True(window.FindControl<CheckBox>("MuteLoopbackVoiceBox")!.IsChecked);
        Assert.False(window.FindControl<CheckBox>("AutoSpeakBox")!.IsEnabled);
        window.FindControl<TextBox>("ApiKeyBox")!.Text = "fake-key";
        window.FindControl<TextBox>("ResponseBox")!.Text = "A reply";
        window.FindControl<TextBox>("VoiceStyleBox")!.Text += " ";
        Assert.False(Button(window, "SpeakButton").IsEnabled);
        window.FindControl<CheckBox>("MuteLoopbackVoiceBox")!.IsChecked = false;
        Assert.True(Button(window, "SpeakButton").IsEnabled);
        Combo(window, "AudioSourceBox").SelectedIndex = 0;
        Assert.Equal(new AudioInputSelection(AudioInputKind.Microphone, Mic.Id), settings.Current);
        Assert.False(window.FindControl<TextBlock>("LoopbackNote")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RefreshPreservesMissingSelectionAndFailuresRetainDevicesWithoutRawErrors()
    {
        var settings = new AudioInputSettings();
        var catalog = new FakeAudioCatalog { Devices = [Mic, Output] };
        var window = Create(catalog, settings);
        window.Show();
        await UntilAsync(() => Combo(window, "MicrophoneDeviceBox").ItemCount == 2);
        Combo(window, "MicrophoneDeviceBox").SelectedIndex = 1;
        catalog.Devices = [];
        Click(window, "RefreshAudioDevicesButton");
        await UntilAsync(() => (Combo(window, "MicrophoneDeviceBox").SelectedItem as AudioDeviceOption)?.Available == false);
        Assert.Equal(Mic.Id, settings.Current.DeviceId);
        Assert.Contains("unavailable", Combo(window, "MicrophoneDeviceBox").SelectedItem!.ToString());
        catalog.Failure = new Exception("private-device-details");
        Click(window, "RefreshAudioDevicesButton");
        await UntilAsync(() => window.FindControl<TextBlock>("AudioDeviceStatus")!.Text!.Contains("Could not list"));
        Assert.Equal(Mic.Id, settings.Current.DeviceId);
        Assert.Equal(2, Combo(window, "MicrophoneDeviceBox").ItemCount);
        Assert.DoesNotContain("private-device-details", window.FindControl<TextBlock>("AudioDeviceStatus")!.Text);
        catalog.Failure = null;
        catalog.Devices = [Mic];
        Click(window, "RefreshAudioDevicesButton");
        await UntilAsync(() => (Combo(window, "MicrophoneDeviceBox").SelectedItem as AudioDeviceOption)?.Available == true);
        Assert.Equal(Mic.Id, settings.Current.DeviceId);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LiveFreezesSelectedLoopbackSourceUploadsAudioAndRestoresControlsAfterStop()
    {
        var settings = new AudioInputSettings();
        var catalog = new FakeAudioCatalog { Devices = [Mic, Output] };
        var transport = new FakeLiveTransport();
        var microphone = new FakeLiveMicrophone();
        AudioInputSelection? snapshot = null;
        var window = Create(catalog, settings, () =>
        {
            snapshot = settings.Current;
            return new GeminiLiveSession(transport, new SilentLivePlayback(), microphone);
        });
        window.Show();
        await UntilAsync(() => Combo(window, "ComputerAudioDeviceBox").ItemCount == 2);
        Combo(window, "AudioSourceBox").SelectedIndex = 1;
        Combo(window, "ComputerAudioDeviceBox").SelectedIndex = 1;
        window.FindControl<TextBox>("ApiKeyBox")!.Text = "fake-key";
        window.FindControl<CheckBox>("UseLiveBox")!.IsChecked = true;
        Click(window, "StartLiveButton");
        transport.Push("{\"setupComplete\":{}}");
        await UntilAsync(() => microphone.Subscriptions == 1);
        Assert.Equal(new AudioInputSelection(AudioInputKind.ComputerAudio, Output.Id), snapshot);
        Assert.False(Combo(window, "AudioSourceBox").IsEnabled);
        Assert.False(Combo(window, "ComputerAudioDeviceBox").IsEnabled);
        Assert.False(Button(window, "RefreshAudioDevicesButton").IsEnabled);
        Assert.False(window.FindControl<CheckBox>("MuteLoopbackVoiceBox")!.IsEnabled);
        Assert.Contains("COMPUTER AUDIO STREAMING TO GOOGLE", window.FindControl<TextBlock>("LiveInputNotice")!.Text);
        microphone.Queue.TryWrite(new byte[] { 1, 2, 3, 4 });
        await UntilAsync(() => transport.Sent.Count == 2);
        Click(window, "StopLiveButton");
        await UntilAsync(() => transport.Disposed && Combo(window, "AudioSourceBox").IsEnabled);
        Assert.Equal(1, microphone.Releases);
        Assert.Equal(snapshot, settings.Current);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LoopbackMutePreservesTextReplyWithoutMakingSpeechRequests()
    {
        int requests = 0;
        using var handler = new SpeechHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Text remains available.\"}]}}]}")
            });
        });
        var window = new MainWindow(new HttpClient(handler), audioDevices: new FakeAudioCatalog());
        window.Show();
        Combo(window, "AudioSourceBox").SelectedIndex = 1;
        window.FindControl<TextBox>("ApiKeyBox")!.Text = "fake-key";
        window.FindControl<TextBox>("TranscriptBox")!.Text = "Video transcript";
        Click(window, "GenerateButton");
        await UntilAsync(() => window.FindControl<TextBox>("ResponseBox")!.Text == "Text remains available.");
        Assert.Equal(1, requests);
        Assert.Equal("Stopped", window.FindControl<TextBlock>("SpeechStatus")!.Text);
        Assert.True(window.FindControl<CheckBox>("AutoSpeakBox")!.IsChecked);
        Assert.False(Button(window, "SpeakButton").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClosingCancelsOutstandingDeviceEnumeration()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeAudioCatalog
        {
            Listing = async token =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return [];
            }
        };
        var window = Create(catalog, new());
        window.Show();
        await entered.Task;
        window.Close();
        await UntilAsync(() => cancelled.Task.IsCompleted && !window.IsVisible);
    }

    private static MainWindow Create(IAudioDeviceCatalog catalog, AudioInputSettings settings, Func<GeminiLiveSession>? factory = null) =>
        new(new HttpClient(new SpeechHttpHandler((_, _) => throw new Xunit.Sdk.XunitException("No network expected."))),
            audioDevices: catalog, audioInput: settings, liveFactory: factory);
    private static ComboBox Combo(MainWindow window, string name) => window.FindControl<ComboBox>(name)!;
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
            if (condition()) return;
            await Task.Delay(5, timeout.Token);
        }
    }
}

internal sealed class FakeAudioCatalog : IAudioDeviceCatalog
{
    public IReadOnlyList<AudioDeviceOption> Devices { get; set; } = [];
    public Exception? Failure { get; set; }
    public Func<CancellationToken, Task<IReadOnlyList<AudioDeviceOption>>>? Listing { get; set; }
    public Task<IReadOnlyList<AudioDeviceOption>> ListAsync(CancellationToken token) =>
        Failure != null ? Task.FromException<IReadOnlyList<AudioDeviceOption>>(Failure) : Listing?.Invoke(token) ?? Task.FromResult(Devices);
}
