using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ContentCoHostStudio;

public partial class MainWindow
{
    private readonly AudioInputSettings audioInput;
    private readonly IAudioDeviceCatalog audioDevices;
    private Task audioDeviceTask = Task.CompletedTask;
    private bool refreshingAudioDevices;
    private bool updatingAudioItems;
    private bool AudioInputBusy => transcription.IsRunning || liveSession?.IsActive == true || !liveTask.IsCompleted;
    private bool ShouldMuteCoHost => audioInput.Current.Kind == AudioInputKind.ComputerAudio && MuteLoopbackVoiceBox?.IsChecked == true;

    private void InitializeAudioControls()
    {
        updatingAudioItems = true;
        AudioSourceBox.ItemsSource = new[] { "Microphone", "Computer audio (loopback)" };
        AudioSourceBox.SelectedIndex = (int)audioInput.Current.Kind;
        SetAudioDeviceItems(MicrophoneDeviceBox, [], AudioInputKind.Microphone);
        SetAudioDeviceItems(ComputerAudioDeviceBox, [], AudioInputKind.ComputerAudio);
        updatingAudioItems = false;
        UpdateAudioControls();
    }

    private void UpdateAudioControls()
    {
        if (AudioSourceBox is null || MicrophoneDeviceBox is null || ComputerAudioDeviceBox is null || MuteLoopbackVoiceBox is null)
            return;
        bool computerAudio = audioInput.Current.Kind == AudioInputKind.ComputerAudio;
        bool editable = !closing && !AudioInputBusy && !refreshingAudioDevices;
        AudioSourceBox.IsEnabled = MicrophoneDeviceBox.IsEnabled = ComputerAudioDeviceBox.IsEnabled =
            RefreshAudioDevicesButton.IsEnabled = MuteLoopbackVoiceBox.IsEnabled = editable;
        MicrophoneDeviceBox.IsVisible = !computerAudio;
        ComputerAudioDeviceBox.IsVisible = LoopbackNote.IsVisible = MuteLoopbackVoiceBox.IsVisible = computerAudio;
        AudioDeviceLabel.Text = computerAudio ? "Playback device to listen to" : "Microphone";
        AutoSpeakBox.IsEnabled = !closing && !IsLiveMode && !ShouldMuteCoHost;
    }

    private void AudioSourceChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updatingAudioItems || AudioDeviceLabel is null || AudioInputBusy)
            return;
        ApplyAudioSelection();
    }

    private void AudioDeviceChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updatingAudioItems || AudioInputBusy || AudioDeviceLabel is null)
            return;
        ApplyAudioSelection();
    }

    private void ApplyAudioSelection()
    {
        bool computerAudio = AudioSourceBox.SelectedIndex == 1;
        var selected = (computerAudio ? ComputerAudioDeviceBox : MicrophoneDeviceBox).SelectedItem as AudioDeviceOption;
        audioInput.Current = new(computerAudio ? AudioInputKind.ComputerAudio : AudioInputKind.Microphone, selected?.Id);
        StopVoice();
        UpdateGenerateButton();
        UpdateAudioControls();
    }

    private void LoopbackMuteChanged(object? sender, RoutedEventArgs args)
    {
        if (SpeechStatus is null || AudioInputBusy)
            return;
        if (ShouldMuteCoHost) StopVoice();
        UpdateSpeechControls();
        UpdateAudioControls();
    }

    private void RefreshAudioDevicesClicked(object? sender, RoutedEventArgs args) => BeginAudioDeviceRefresh();

    private void BeginAudioDeviceRefresh()
    {
        if (closing || AudioInputBusy || refreshingAudioDevices)
            return;
        refreshingAudioDevices = true;
        UpdateAudioControls();
        audioDeviceTask = RefreshAudioDevicesAsync();
    }

    private async Task RefreshAudioDevicesAsync()
    {
        try
        {
            var devices = await audioDevices.ListAsync(closingSource.Token);
            if (closing) return;
            updatingAudioItems = true;
            SetAudioDeviceItems(MicrophoneDeviceBox, devices, AudioInputKind.Microphone);
            SetAudioDeviceItems(ComputerAudioDeviceBox, devices, AudioInputKind.ComputerAudio);
            AudioDeviceStatus.Text = "Devices refreshed. Stop local listening and Live before changing inputs. Selections are memory only.";
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception)
        {
            if (!closing) AudioDeviceStatus.Text = "Could not list audio devices. Previous selections were retained. Check Windows audio devices and try Refresh Audio Devices.";
        }
        finally
        {
            updatingAudioItems = false;
            refreshingAudioDevices = false;
            UpdateAudioControls();
        }
    }

    private void SetAudioDeviceItems(ComboBox box, IReadOnlyList<AudioDeviceOption> devices, AudioInputKind kind)
    {
        var previous = box.SelectedItem as AudioDeviceOption;
        if (previous == null && audioInput.Current.Kind == kind && audioInput.Current.DeviceId is { } id)
            previous = new(id, "Selected device", kind);
        var options = new List<AudioDeviceOption> { AudioDeviceOption.Default(kind) };
        options.AddRange(devices.Where(device => device.Kind == kind && device.Id != null)
            .DistinctBy(device => device.Id).OrderBy(device => device.Name));
        if (previous?.Id != null && options.All(option => option.Id != previous.Id))
            options.Add(previous with { Available = false, Name = previous.Name.Replace(" (unavailable)", "") + " (unavailable)" });
        box.ItemsSource = options;
        box.SelectedItem = options.First(option => option.Id == previous?.Id);
    }
}
