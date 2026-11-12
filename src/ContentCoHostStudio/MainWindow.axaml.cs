using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ContentCoHostStudio;

public partial class MainWindow : Window
{
    private readonly TranscriptState transcript = new();
    private readonly TranscriptionService transcription;
    private readonly HttpClient httpClient;
    private readonly CancellationTokenSource closingSource = new();
    private readonly GeminiService gemini;
    private Task? generationTask;
    private bool generating;
    private bool closing;
    private bool cleanupComplete;
    private int generationRequest;

    public MainWindow() : this(GeminiService.CreateClient()) { }

    // A supplied client lets UI tests verify the complete flow without paid API calls.
    public MainWindow(HttpClient httpClient, ISpeechPlayback? speechPlayback = null,
        Func<GeminiLiveSession>? liveFactory = null, IMicrophoneSource? microphone = null,
        IAudioDeviceCatalog? audioDevices = null, AudioInputSettings? audioInput = null,
        AvatarPackLibrary? avatarPacks = null, ISpoutOutput? spoutOutput = null, PlaybackActivityHub? playbackActivity = null, TimeProvider? visitClock = null)
    {
        this.playbackActivity = playbackActivity ?? new PlaybackActivityHub();
        avatarLibrary = avatarPacks ?? new AvatarPackLibrary();
        avatarPack = avatarLibrary.Starter;
        spout = spoutOutput ?? new SpoutOutputService(avatarPack);
        this.audioInput = audioInput ?? new AudioInputSettings();
        this.audioDevices = audioDevices ?? new WindowsAudioDeviceCatalog();
        var sharedMicrophone = microphone ?? new MicrophoneCaptureService(new WindowsMicrophoneCapture(this.audioInput));
        transcription = new TranscriptionService(microphone: sharedMicrophone);
        this.liveFactory = liveFactory ?? (() => new GeminiLiveSession(new LiveWebSocketTransport(), new LiveAudioPlayback(ShouldMuteCoHost, this.playbackActivity), sharedMicrophone));
        this.httpClient = httpClient;
        this.speechPlayback = speechPlayback ?? new SpeechPlayback(this.playbackActivity);
        gemini = new GeminiService(httpClient);
        InitializeComponent();
        InitializeVisitControls(visitClock);
        InitializeModelControls();
        InitializeSpeechControls();
        InitializeAudioControls();
        InitializeAvatarControls();
        LiveBehaviorBox.Text = GeminiLiveSession.DefaultBehavior;
        UpdateLiveControls();
        PersonalityBox.Text = GeminiService.DefaultPersonality;

        transcription.Caption += (generation, text, isFinal) => Dispatcher.UIThread.Post(() =>
        {
            if (closing)
                return;
            transcript.SavedText = TranscriptBox.Text ?? "";
            transcript.Apply(generation, text, isFinal);
            CaptionsText.Text = transcript.PartialText;
            if (isFinal && generation == transcript.Generation)
                TranscriptBox.Text = transcript.SavedText;
        });
        transcription.Status += status => Dispatcher.UIThread.Post(() => SetListeningStatus(status));
        transcription.Error += message => Dispatcher.UIThread.Post(() =>
        {
            MicrophoneError.Text = message;
            MicrophoneError.IsVisible = true;
        });
        Closing += OnClosing;
        Opened += (_, _) => { BeginAudioDeviceRefresh(); BeginAvatarOperation(LoadAvatarLibraryAsync); };
    }

    private void SetListeningStatus(string status)
    {
        ListeningStatus.Text = status;
        ListenButton.Content = status == "Stopped" ? "Start Listening" : "Stop Listening";
        ListenButton.IsEnabled = !closing && status != "Stopping…" && !(liveSession?.IsActive == true && liveSession.IsMicrophone);
        if (status == "Stopped")
        {
            transcript.ClearPartial();
            CaptionsText.Text = "";
        }
        UpdateAudioControls();
    }

    private async void ListenClicked(object? sender, RoutedEventArgs args)
    {
        if (liveSession?.IsActive == true && liveSession.IsMicrophone)
            return;
        if (transcription.IsRunning)
        {
            ListenButton.IsEnabled = false;
            await transcription.StopAsync();
            SetListeningStatus("Stopped");
            return;
        }

        MicrophoneError.IsVisible = false;
        transcript.ClearPartial();
        transcription.Start(transcript.Generation);
        UpdateAudioControls();
    }

    private void ClearClicked(object? sender, RoutedEventArgs args)
    {
        int generation = transcript.Clear();
        transcription.Reset(generation);
        TranscriptBox.Text = "";
        CaptionsText.Text = "";
    }

    private void InputChanged(object? sender, TextChangedEventArgs args)
        => UpdateGenerateButton();

    private void UpdateGenerateButton()
    {
        // XAML can fire TextChanged before all named controls have been initialized.
        if (GenerateButton is null || ApiKeyBox is null || TranscriptBox is null)
            return;
        GenerateButton.IsEnabled = !closing && !generating && !IsLiveMode &&
            !string.IsNullOrWhiteSpace(ApiKeyBox.Text) && !string.IsNullOrWhiteSpace(TranscriptBox.Text) &&
            SelectedModel(TextModelBox)?.IsSupported == true;
        UpdateSpeechControls();
        UpdateLiveControls();
    }

    private async void GenerateClicked(object? sender, RoutedEventArgs args)
    {
        if (generating || closing || IsLiveMode)
            return;

        StopVoice();
        generating = true;
        GenerateButton.IsEnabled = false;
        GeminiError.IsVisible = false;
        GenerationStatus.Text = "Generating…";
        int requestId = ++generationRequest;
        textTiming.Start();
        UpdateTimingLabels();
        UpdateSpeechControls();
        // Strings are immutable snapshots: later edits and recognition cannot change this request.
        generationTask = GenerateAsync(ApiKeyBox.Text ?? "", PersonalityBox.Text ?? "", TranscriptBox.Text ?? "", selectedTextModel, requestId);
        await generationTask;
    }

    private async Task GenerateAsync(string key, string personality, string savedTranscript, string model, int requestId)
    {
        try
        {
            string response = await gemini.GenerateAsync(key, personality, savedTranscript, closingSource.Token, model);
            if (!closing && requestId == generationRequest)
            {
                ResponseBox.Text = response;
                ResponseHeading.Text = $"Gemini Response · {model}";
                LastTextModel.Text = $"Last text model: {model}";
                textTiming.Finish("Completed");
                if (AutoSpeakBox.IsChecked == true)
                    StartSpeech();
            }
        }
        catch (OperationCanceledException) when (closingSource.IsCancellationRequested)
        {
            if (requestId == generationRequest)
                textTiming.Finish("Cancelled");
        }
        catch (InvalidOperationException exception)
        {
            if (!closing)
            {
                GeminiError.Text = exception.Message;
                GeminiError.IsVisible = true;
                textTiming.Finish("Failed");
            }
        }
        catch (Exception)
        {
            if (!closing)
            {
                GeminiError.Text = "The request could not be completed. Check your connection and try again.";
                GeminiError.IsVisible = true;
                textTiming.Finish("Failed");
            }
        }
        finally
        {
            generating = false;
            GenerationStatus.Text = "";
            UpdateTimingLabels();
            UpdateGenerateButton();
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (cleanupComplete)
            return;
        args.Cancel = true;
        if (closing)
            return;

        closing = true;
        avatarTimer.Stop();
        if (desktopAvatar != null) { desktopAvatar.AutomaticPresentation = false; desktopAvatar.Close(); }
        avatarDialog?.Close();
        playbackActivity.Reset();
        ListenButton.IsEnabled = false;
        GenerateButton.IsEnabled = false;
        ClearButton.IsEnabled = false;
        timingTimer.Stop();
        UpdateRefreshButton();
        StopVoice();
        closingSource.Cancel();
        await visits.StopAsync();
        await StopLiveAsync();
        await liveTask;
        await transcription.StopAsync();
        if (generationTask != null)
            await generationTask;
        await speechTask;
        await Task.WhenAll(catalogTasks);
        await audioDeviceTask;
        await avatarTask;
        await spoutTask;
        await spout.DisposeAsync();
        spriteImages.Dispose();
        cachedSpeech = null;
        httpClient.Dispose();
        closingSource.Dispose();
        ApiKeyBox.Text = "";
        cleanupComplete = true;
        Close();
    }
}
