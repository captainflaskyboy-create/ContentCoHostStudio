using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ContentCoHostStudio;

public partial class MainWindow
{
    private readonly ISpeechPlayback speechPlayback;
    private CancellationTokenSource? speechSource;
    private Task speechTask = Task.CompletedTask;
    private int speechRequest;
    private bool speechActive;
    private CachedSpeech? cachedSpeech;

    private void InitializeSpeechControls()
    {
        VoiceBox.ItemsSource = GeminiSpeechService.Voices;
        VoiceBox.SelectedItem = GeminiSpeechService.DefaultVoice;
        VoicePresetBox.ItemsSource = new[] { "Sarcastic Droid", "Sarcastic Robot", "Melancholy Documentary" };
        VoicePresetBox.SelectedIndex = 0;
        VoiceStyleBox.Text = GeminiSpeechService.DroidStyle;
        UpdateSpeechControls();
    }

    private void VoicePresetChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (VoiceStyleBox is not null)
            VoiceStyleBox.Text = (VoicePresetBox.SelectedItem as string) switch
            {
                "Melancholy Documentary" => GeminiSpeechService.DocumentaryStyle,
                "Sarcastic Robot" => GeminiSpeechService.RobotStyle,
                _ => GeminiSpeechService.DroidStyle
            };
    }

    private void AutoSpeakChanged(object? sender, RoutedEventArgs args)
    {
        if (AutoSpeakBox?.IsChecked == false && SpeechStatus is not null)
            StopVoice();
    }

    private void VoiceSettingsChanged(object? sender, SelectionChangedEventArgs args) => UpdateSpeechControls();

    private void SpeakClicked(object? sender, RoutedEventArgs args) => StartSpeech();
    private void StopVoiceClicked(object? sender, RoutedEventArgs args) => StopVoice();

    private void UpdateSpeechControls()
    {
        if (SpeakButton is null || ResponseBox is null || VoiceBox is null)
            return;
        bool hasCachedAudio = cachedSpeech?.Matches(ResponseBox.Text ?? "", VoiceBox.SelectedItem as string ?? GeminiSpeechService.DefaultVoice, VoiceStyleBox.Text ?? "", selectedSpeechModel) == true;
        SpeakButton.IsEnabled = !closing && !generating && !IsLiveMode && !ShouldMuteCoHost && !string.IsNullOrWhiteSpace(ResponseBox.Text) &&
            (hasCachedAudio || !string.IsNullOrWhiteSpace(ApiKeyBox.Text)) && SelectedModel(SpeechModelBox)?.IsSupported == true;
        StopVoiceButton.IsEnabled = !closing && speechActive;
    }

    private void StartSpeech()
    {
        if (closing || IsLiveMode || ShouldMuteCoHost || string.IsNullOrWhiteSpace(ResponseBox.Text))
            return;
        Task previousSpeech = speechTask;
        StopVoice();
        var source = CancellationTokenSource.CreateLinkedTokenSource(closingSource.Token);
        speechSource = source;
        speechActive = true;
        voiceTiming.Start();
        UpdateTimingLabels();
        SpeechError.IsVisible = false;
        SpeechStatus.Text = "Preparing Voice";
        UpdateSpeechControls();
        // Each operation snapshots its own text/settings. Awaiting the old operation prevents overlap.
        speechTask = SpeakResponseAsync(previousSpeech, speechRequest, ApiKeyBox.Text ?? "", ResponseBox.Text ?? "",
            VoiceBox.SelectedItem as string ?? GeminiSpeechService.DefaultVoice, VoiceStyleBox.Text ?? "", selectedSpeechModel, source);
    }

    private void StopVoice()
    {
        playbackActivity.Reset();
        voiceTiming.Finish("Cancelled");
        UpdateTimingLabels();
        ++speechRequest;
        speechSource?.Cancel();
        speechActive = false;
        SpeechStatus.Text = "Stopped";
        UpdateSpeechControls();
    }

    private async Task SpeakResponseAsync(Task previousSpeech, int requestId, string key, string text, string voice, string style, string model,
        CancellationTokenSource source)
    {
        CancellationToken token = source.Token;
        try
        {
            await previousSpeech;
            token.ThrowIfCancellationRequested();
            byte[] wav;
            if (cachedSpeech?.Matches(text, voice, style, model) == true)
            {
                wav = cachedSpeech.Audio;
                voiceTiming.Cached();
            }
            else
                wav = await Task.Run(() => new GeminiSpeechService(httpClient, key).SynthesizeAsync(text, voice, style, token, model), token);

            token.ThrowIfCancellationRequested();
            if (closing || requestId != speechRequest)
                return;
            voiceTiming.Finish("Completed");
            UpdateTimingLabels();
            LastSpeechModel.Text = $"Last audio model: {model}";
            cachedSpeech = new CachedSpeech(text, voice, style, model, wav);
            SpeechStatus.Text = "Speaking";
            await Task.Run(() => speechPlayback.PlayAsync(wav, token), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!closing && requestId == speechRequest)
            {
                SpeechError.Text = exception is InvalidOperationException ? exception.Message :
                    "Could not prepare or play the voice. Check your connection and default output device, then try again.";
                SpeechError.IsVisible = true;
                voiceTiming.Finish("Failed");
                UpdateTimingLabels();
            }
        }
        finally
        {
            if (ReferenceEquals(speechSource, source))
                speechSource = null;
            source.Dispose();
            if (requestId == speechRequest)
            {
                speechActive = false;
                SpeechStatus.Text = "Stopped";
                UpdateSpeechControls();
            }
        }
    }

    private sealed record CachedSpeech(string Text, string Voice, string Style, string Model, byte[] Audio)
    {
        public bool Matches(string text, string voice, string style, string model) => Text == text && Voice == voice && Style == style && Model == model;
    }
}
