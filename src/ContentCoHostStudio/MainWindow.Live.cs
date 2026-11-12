using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ContentCoHostStudio;

public partial class MainWindow
{
    private readonly Func<GeminiLiveSession> liveFactory;
    private GeminiLiveSession? liveSession;
    private Task liveTask = Task.CompletedTask;
    private int liveRequest;
    private bool liveCleanupFailed;
    private bool IsLiveMode => UseLiveBox?.IsChecked == true;

    private async void LiveModeChanged(object? sender, RoutedEventArgs args)
    {
        if (LivePanel is null)
            return;
        StopVoice();
        bool wasActive = liveSession?.IsActive == true;
        int request = ++liveRequest;
        await visits.StopAsync();
        await StopLiveAsync();
        if (!closing && wasActive && request == liveRequest && liveSession != null)
            ResponseBox.Text = liveSession.ResponseText;
        UpdateGenerateButton();
        UpdateLiveControls();
    }

    private async void LiveInputChanged(object? sender, RoutedEventArgs args)
    {
        if (LivePanel is null)
            return;
        bool wasActive = liveSession?.IsActive == true;
        int request = ++liveRequest;
        await visits.StopAsync();
        await StopLiveAsync();
        if (!closing && wasActive && request == liveRequest && liveSession != null)
            ResponseBox.Text = liveSession.ResponseText;
        UpdateLiveControls();
    }

    private void UpdateLiveControls()
    {
        if (visits is null || LivePanel is null || LiveBehaviorBox is null || ListenButton is null)
            return;
        bool active = liveSession?.IsActive == true;
        bool busy = active || !liveTask.IsCompleted;
        LiveModelBox.IsEnabled = !closing && !busy;
        LiveModelHelp.Text = "Affects the next Start Live only. Refresh Models verifies the options for your key. " + GeminiModelCatalog.LiveExplanation(selectedLiveModel);
        bool mic = LiveMicrophoneBox.IsChecked == true;
        bool computerAudio = audioInput.Current.Kind == AudioInputKind.ComputerAudio;
        string inputName = computerAudio ? "COMPUTER AUDIO" : "MICROPHONE";
        UpdateAudioControls();
        LivePanel.IsVisible = IsLiveMode;
        UseLiveBox.IsEnabled = !closing && !generating;
        StartLiveButton.IsEnabled = !visits.OwnsLive && ValidVisitSettings() && IsLiveMode && !closing && !generating && !busy && speechTask.IsCompleted &&
            !string.IsNullOrWhiteSpace(ApiKeyBox.Text) && SelectedModel(LiveModelBox)?.IsSupported == true && (mic || !string.IsNullOrWhiteSpace(TranscriptBox.Text));
        StopLiveButton.IsEnabled = !closing && busy;
        SendTranscriptButton.IsVisible = !mic;
        SendTranscriptButton.IsEnabled = !closing && IsLiveMode && liveSession?.CanSendText == true &&
            !string.IsNullOrWhiteSpace(TranscriptBox.Text);
        PersonalityBox.IsEnabled = VoiceBox.IsEnabled = VoicePresetBox.IsEnabled = VoiceStyleBox.IsEnabled =
            LiveBehaviorBox.IsEnabled = ApiKeyBox.IsEnabled = !closing && !busy;
        TextModelBox.IsEnabled = SpeechModelBox.IsEnabled = !closing && !IsLiveMode;
        AutoSpeakBox.IsEnabled = !closing && !IsLiveMode && !ShouldMuteCoHost;
        ListenButton.IsEnabled = !closing && ListeningStatus.Text != "Stopping…" && !(busy && liveSession?.IsMicrophone == true);
        LiveInputNotice.Text = active && liveSession!.IsMicrophone && liveSession.State == LiveSessionState.Connecting
            ? "Connecting to Google · selected audio upload starts after setup completes"
            : active && liveSession!.IsMicrophone
            ? $"{inputName} STREAMING TO GOOGLE · local captions remain separate"
            : mic ? $"{inputName} is not streaming to Google. Start Live enables cloud streaming for the configured Live duration."
            : "Saved transcript only · Live never uploads audio in this mode";
        UpdateVisitControls(busy);
        if (liveSession != null)
        {
            LiveStatus.Text = $"Live: {liveSession.State} · {liveSession.SessionModel}";
            LiveCountdown.Text = active
                ? $"{Math.Max(0, liveSession.Duration.TotalSeconds - liveSession.Elapsed.TotalSeconds):0.0} seconds remaining · elapsed {liveSession.Elapsed.TotalSeconds:0.0}s"
                : $"Session ended after {liveSession.Elapsed.TotalSeconds:0.0}s · configured limit {liveSession.Duration.TotalSeconds:0}s";
            string connection = liveSession.ConnectionTime is { } elapsed ? $"{elapsed.TotalSeconds:0.00}s" : "not connected";
            LiveTimingLabel.Text = $"Connection: {connection}" + (liveSession.IsMicrophone ? " · Audio input mode: silence is not response latency" :
                liveSession.FirstAudioTime is { } first ? $" · Text send → first audio: {first.TotalSeconds:0.00}s" : " · Text send → first audio: waiting / unavailable");
        }
        else if (ValidVisitSettings()) LiveCountdown.Text = $"Configured Live window: {SnapshotLiveOptions().DurationSeconds} seconds";
    }

    private void StartLiveClicked(object? sender, RoutedEventArgs args)
    {
        UpdateLiveControls();
        if (!StartLiveButton.IsEnabled)
            return;
        BeginLive(false, closingSource.Token);
    }

    private void BeginLive(bool automatic, CancellationToken token)
    {
        StopVoice();
        cachedSpeech = null;
        LiveError.IsVisible = false;
        ResponseBox.Text = "";
        ResponseHeading.Text = $"Gemini Live Response · {selectedLiveModel}";
        int request = ++liveRequest;
        var settings = SnapshotLiveOptions();
        liveCleanupFailed = false;
        var session = liveFactory();
        liveSession = session;
        automaticVisit = automatic;
        automaticSpeaking = false;
        lastVisitTurn = -1;
        session.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (!closing && request == liveRequest)
            {
                if (!session.IsActive) { automaticSpeaking = false; UpdateAvatarFrame(); }
                UpdateLiveControls();
            }
        });
        session.PlaybackChanged += turn => Dispatcher.UIThread.Post(() =>
        {
            if (closing || request != liveRequest || !automaticVisit || !session.IsActive || turn.Id < lastVisitTurn) return;
            lastVisitTurn = turn.Id;
            automaticSpeaking = turn.Speaking;
            UpdateAvatarFrame(); UpdateLiveControls();
        });
        session.ResponseChanged += text => Dispatcher.UIThread.Post(() =>
        {
            if (!closing && request == liveRequest) ResponseBox.Text = text;
        });
        session.Error += error => Dispatcher.UIThread.Post(() =>
        {
            if (closing || request != liveRequest) return;
            LiveError.Text = error; LiveError.IsVisible = true;
        });
        liveTask = RunLiveAsync(session, ApiKeyBox.Text ?? "", settings, TranscriptBox.Text ?? "", token);
        UpdateLiveControls(); UpdateAvatarFrame();
    }

    private async Task RunLiveAsync(GeminiLiveSession session, string key, LiveSessionOptions settings, string savedText, CancellationToken token)
    {
        try { await session.RunAsync(key, settings, savedText, token); }
        finally
        {
            try { await session.DisposeAsync(); }
            catch (Exception)
            {
                liveCleanupFailed = true;
                if (!closing)
                {
                    LiveError.Text = "Live audio cleanup failed. Check input/output devices and restart Live or Resume Automatic Visits. The connection was released.";
                    LiveError.IsVisible = true;
                }
            }
            automaticSpeaking = false;
            UpdateAvatarFrame();
            UpdateLiveControls();
            // This task is still busy until its finally block returns.
            Dispatcher.UIThread.Post(UpdateLiveControls);
        }
    }

    private async void StopLiveClicked(object? sender, RoutedEventArgs args)
    {
        if (automaticVisit && visits.OwnsLive) visits.Dismiss();
        await StopLiveAsync();
        UpdateLiveControls();
    }

    private async Task StopLiveAsync()
    {
        automaticSpeaking = false; UpdateAvatarFrame();
        playbackActivity.Reset();
        if (liveSession != null)
            await liveSession.StopAsync();
    }

    private async void SendLiveTranscriptClicked(object? sender, RoutedEventArgs args)
    {
        var session = liveSession;
        if (session?.CanSendText != true || closing || !IsLiveMode)
            return;
        int request = liveRequest;
        try { await session.SendTextAsync(TranscriptBox.Text ?? "", closingSource.Token); }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!closing && request == liveRequest)
            {
                LiveError.Text = "Could not send the transcript. Start a new Live session when ready.";
                LiveError.IsVisible = true;
            }
            await session.StopAsync();
        }
        UpdateLiveControls();
    }
}
