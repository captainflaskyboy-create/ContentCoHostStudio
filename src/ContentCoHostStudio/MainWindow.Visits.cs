using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ContentCoHostStudio;

public partial class MainWindow
{
    private LiveVisitSchedule visits = null!;
    private bool automaticVisit, automaticSpeaking, visitPresentation;
    private long lastVisitTurn;
    private bool previousDesktopVisible;
    private StreamerWindow? previousDesktop;
    private bool changingVisitUnits;
    private int intervalUnit = 1, durationUnit;

    private void InitializeVisitControls(TimeProvider? clock)
    {
        visits = new(RunScheduledVisitAsync, ReadVisitInterval, clock);
        visits.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (closing) return;
            UpdateVisitPresentation(); UpdateLiveControls(); UpdateAvatarFrame();
        });
        VisitIntervalUnit.ItemsSource = LiveDurationUnit.ItemsSource = new[] { "Seconds", "Minutes" };
        VisitIntervalUnit.SelectedIndex = 1; LiveDurationUnit.SelectedIndex = 0;
        LiveReplyLengthBox.ItemsSource = new[] { "One word", "One sentence", "Custom maximum words" };
        LiveReplyLengthBox.SelectedIndex = 1;
        VisitIntervalUnit.SelectionChanged += (_, _) => ChangeVisitUnits(VisitIntervalValue, VisitIntervalUnit, ref intervalUnit, 86400);
        LiveDurationUnit.SelectionChanged += (_, _) => ChangeVisitUnits(LiveDurationValue, LiveDurationUnit, ref durationUnit, 600);
        LiveReplyLengthBox.SelectionChanged += (_, _) => UpdateLiveControls();
        VisitIntervalValue.ValueChanged += (_, _) => UpdateLiveControls();
        LiveDurationValue.ValueChanged += (_, _) => UpdateLiveControls();
        LiveMaximumWords.ValueChanged += (_, _) => UpdateLiveControls();
    }
    private void ChangeVisitUnits(NumericUpDown value, ComboBox unit, ref int previous, int maximumSeconds)
    {
        if (changingVisitUnits || unit.SelectedIndex < 0) return;
        changingVisitUnits = true;
        decimal seconds = (value.Value ?? 1) * (previous == 1 ? 60 : 1);
        previous = unit.SelectedIndex;
        decimal multiplier = previous == 1 ? 60 : 1;
        value.Minimum = 1m / multiplier; value.Maximum = maximumSeconds / multiplier;
        value.Value = Math.Clamp(seconds / multiplier, value.Minimum, value.Maximum);
        value.Increment = previous == 1 ? .5m : 1m;
        changingVisitUnits = false; UpdateLiveControls();
    }
    private TimeSpan ReadVisitInterval()
    {
        var value = TimeSpan.FromSeconds((double)((VisitIntervalValue.Value ?? 0) * (VisitIntervalUnit.SelectedIndex == 1 ? 60 : 1)));
        LiveVisitSchedule.ValidateInterval(value); return value;
    }
    private LiveSessionOptions SnapshotLiveOptions()
    {
        var settings = new LiveSessionOptions(LiveMicrophoneBox.IsChecked == true, PersonalityBox.Text ?? "",
            VoiceBox.SelectedItem as string ?? GeminiSpeechService.DefaultVoice, VoiceStyleBox.Text ?? "", LiveBehaviorBox.Text ?? "", selectedLiveModel,
            (int)Math.Round((LiveDurationValue.Value ?? 0) * (LiveDurationUnit.SelectedIndex == 1 ? 60 : 1)),
            (LiveReplyLength)LiveReplyLengthBox.SelectedIndex, (int)(LiveMaximumWords.Value ?? 20));
        settings.Validate(); return settings;
    }
    private bool ValidVisitSettings()
    {
        try { SnapshotLiveOptions(); ReadVisitInterval(); return true; }
        catch (Exception) { return false; }
    }
    private void UpdateVisitControls(bool busy)
    {
        bool valid = ValidVisitSettings();
        LiveDurationValue.IsEnabled = LiveDurationUnit.IsEnabled = LiveReplyLengthBox.IsEnabled = LiveMaximumWords.IsEnabled = !closing && !busy;
        LiveMaximumWords.IsVisible = LiveReplyLengthBox.SelectedIndex == 2;
        VisitIntervalValue.IsEnabled = VisitIntervalUnit.IsEnabled = !closing && !busy;
        StartVisitsButton.Content = visits.State == LiveVisitState.Paused ? "Resume Automatic Visits" : "Start Automatic Visits";
        bool ready = IsLiveMode && LiveMicrophoneBox.IsChecked == true && valid && !generating && speechTask.IsCompleted &&
            !string.IsNullOrWhiteSpace(ApiKeyBox.Text) && SelectedModel(LiveModelBox)?.IsSupported == true;
        StartVisitsButton.IsEnabled = !closing && !busy && ready && visits.State is LiveVisitState.Off or LiveVisitState.Paused;
        StopVisitsButton.IsEnabled = !closing && visits.OwnsLive;
        VisitNowButton.IsEnabled = !closing && ready && !busy && visits.State == LiveVisitState.Waiting;
        VisitStatus.Text = !valid ? "Enter a valid interval (1 second–24 hours), duration (1–600 seconds), and word limit (1–100)." : visits.State switch
        {
            LiveVisitState.Waiting => $"Waiting · next visit in {visits.Remaining.TotalSeconds:0.0}s · no audio streaming to Google",
            LiveVisitState.Active => liveSession?.IsActive == false ? "Stopping · automatic visit" : liveSession?.State == LiveSessionState.Connecting ? "Connecting · automatic visit" : automaticSpeaking ? "Speaking · automatic visit" : "Listening · automatic visit",
            LiveVisitState.Stopping => "Stopping · automatic visit",
            LiveVisitState.Paused => "Automatic visits paused after a failure. Check the error and settings, then Resume.",
            _ => "Automatic visits off"
        };
    }
    private void StartVisitsClicked(object? sender, RoutedEventArgs args)
    {
        UpdateLiveControls(); if (!StartVisitsButton.IsEnabled) return;
        LiveError.IsVisible = false; visits.Start();
        UpdateVisitPresentation(); UpdateLiveControls(); UpdateAvatarFrame();
    }
    private async void StopVisitsClicked(object? sender, RoutedEventArgs args)
    {
        automaticSpeaking = false; UpdateAvatarFrame();
        await visits.StopAsync();
        UpdateVisitPresentation(); UpdateLiveControls(); UpdateAvatarFrame();
    }
    private void VisitNowClicked(object? sender, RoutedEventArgs args)
    {
        UpdateLiveControls(); if (VisitNowButton.IsEnabled) visits.VisitNow();
    }
    private async Task<bool> RunScheduledVisitAsync(CancellationToken token)
    {
        Task work = Task.CompletedTask;
        GeminiLiveSession? session = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (closing || !IsLiveMode || LiveMicrophoneBox.IsChecked != true || !ValidVisitSettings() || !liveTask.IsCompleted ||
                string.IsNullOrWhiteSpace(ApiKeyBox.Text) || SelectedModel(LiveModelBox)?.IsSupported != true)
            {
                LiveError.Text = "Automatic visit could not start. Check Live mode, audio input, key, model, and duration before resuming.";
                LiveError.IsVisible = true; return;
            }
            BeginLive(true, token); session = liveSession; work = liveTask;
        });
        try { await work; }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                automaticSpeaking = false; automaticVisit = false;
                if (!closing) { UpdateAvatarFrame(); UpdateLiveControls(); }
            });
        }
        return session != null && session.State != LiveSessionState.Failed && !liveCleanupFailed;
    }
    private void UpdateVisitPresentation()
    {
        bool owned = visits.OwnsLive;
        if (owned && !visitPresentation)
        {
            previousDesktop = desktopAvatar; previousDesktopVisible = desktopAvatar?.IsVisible == true;
            visitPresentation = true;
            EnsureDesktopAvatar(); desktopAvatar!.AutomaticPresentation = true; desktopAvatar.Hide();
        }
        else if (!owned && visitPresentation)
        {
            visitPresentation = false; automaticSpeaking = false; automaticVisit = false;
            if (desktopAvatar != null)
            {
                desktopAvatar.AutomaticPresentation = false;
                if (previousDesktopVisible && ReferenceEquals(previousDesktop, desktopAvatar)) desktopAvatar.Show();
                else desktopAvatar.Hide();
            }
        }
    }
    private async void DismissVisit(object? sender, EventArgs args)
    {
        if (!visitPresentation) return;
        automaticSpeaking = false; visits.Dismiss(); UpdateAvatarFrame();
        await StopLiveAsync(); UpdateLiveControls();
    }
}
