using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class SpeechWindowTests
{
    [AvaloniaFact]
    public void VoiceControlsHaveTheRequestedDefaultsAndPresets()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            Assert.True(window.FindControl<CheckBox>("AutoSpeakBox")!.IsChecked);
            Assert.Equal("Puck", window.FindControl<ComboBox>("VoiceBox")!.SelectedItem);
            Assert.Equal("Sarcastic Droid", window.FindControl<ComboBox>("VoicePresetBox")!.SelectedItem);
            const string approvedStyle = "A crisp, articulate male baritone with a dry, slightly metallic resonance and a refined mid-Atlantic accent. " +
                "The tone is deadpan, unhurried, and emotionally detached—sounding like an exhausted, hyper-intelligent droid who finds everything mildly beneath him.\n" +
                "Clear enunciation, low vocal warmth, and a naturally flat, matter-of-fact cadence.";
            Assert.Equal(approvedStyle, window.FindControl<TextBox>("VoiceStyleBox")!.Text);
            var modelItem = (ComboBoxItem)window.FindControl<ComboBox>("SpeechModelBox")!.SelectedItem!;
            Assert.Equal("gemini-3.8-flash-tts", ((GeminiModelOption)modelItem.Tag!).Id);
            window.FindControl<ComboBox>("VoicePresetBox")!.SelectedItem = "Melancholy Documentary";
            Assert.Equal(GeminiSpeechService.DocumentaryStyle, window.FindControl<TextBox>("VoiceStyleBox")!.Text);
            window.FindControl<ComboBox>("VoicePresetBox")!.SelectedItem = "Sarcastic Droid";
            Assert.Equal(approvedStyle, window.FindControl<TextBox>("VoiceStyleBox")!.Text);
            Assert.False(window.FindControl<Button>("SpeakButton")!.IsEnabled);
            Assert.False(window.FindControl<Button>("StopVoiceButton")!.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AutoSpeakDisplaysTextThenPlaysAudioAndLeavesMicrophoneControlsEnabled()
    {
        var releaseAudio = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SpeechHttpHandler((request, _) => request.RequestUri == GeminiSpeechService.Endpoint ?
            releaseAudio.Task : Task.FromResult(TextResponse()));
        var player = new SilentPlayer { HoldPlayback = true };
        var window = CreateWindow(handler, player);
        try
        {
            Click(window, "GenerateButton");
            await WaitAsync(() => window.FindControl<TextBlock>("SpeechStatus")!.Text == "Preparing Voice");
            Assert.Equal("A robot reply.", window.FindControl<TextBox>("ResponseBox")!.Text);
            Assert.True(window.FindControl<Button>("ListenButton")!.IsEnabled);
            Assert.True(window.FindControl<TextBox>("TranscriptBox")!.IsEnabled);
            releaseAudio.SetResult(SpeechTestAudio.Response());
            await WaitAsync(() => player.PlayCount == 1);
            Assert.Equal("Speaking", window.FindControl<TextBlock>("SpeechStatus")!.Text);
            Assert.True(window.FindControl<Button>("StopVoiceButton")!.IsEnabled);
            player.Finish();
            await WaitAsync(() => window.FindControl<TextBlock>("SpeechStatus")!.Text == "Stopped");
        }
        finally { await CloseAsync(window); }
    }

    [AvaloniaFact]
    public async Task ManualSpeakCachesAudioAndChangedSettingsGenerateFreshAudio()
    {
        int speechRequests = 0;
        using var handler = new SpeechHttpHandler((request, _) =>
        {
            if (request.RequestUri != GeminiSpeechService.Endpoint)
                return Task.FromResult(TextResponse());
            Interlocked.Increment(ref speechRequests);
            return Task.FromResult(SpeechTestAudio.Response());
        });
        var player = new SilentPlayer();
        var window = CreateWindow(handler, player);
        window.FindControl<CheckBox>("AutoSpeakBox")!.IsChecked = false;
        try
        {
            Click(window, "GenerateButton");
            await WaitAsync(() => !string.IsNullOrWhiteSpace(window.FindControl<TextBox>("ResponseBox")!.Text));
            Assert.Equal(0, speechRequests);
            Click(window, "SpeakButton");
            await WaitAsync(() => player.PlayCount == 1 && IsStopped(window));
            Click(window, "SpeakButton");
            await WaitAsync(() => player.PlayCount == 2 && IsStopped(window));
            Assert.Equal(1, speechRequests);
            window.FindControl<TextBox>("ApiKeyBox")!.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Button>("SpeakButton")!.IsEnabled);
            window.FindControl<TextBox>("ApiKeyBox")!.Text = "fake-ui-key";
            window.FindControl<ComboBox>("VoiceBox")!.SelectedItem = "Charon";
            Click(window, "SpeakButton");
            await WaitAsync(() => player.PlayCount == 3 && IsStopped(window));
            window.FindControl<TextBox>("VoiceStyleBox")!.Text = "A new style.";
            Dispatcher.UIThread.RunJobs();
            Click(window, "SpeakButton");
            await WaitAsync(() => player.PlayCount == 4 && IsStopped(window));
            Assert.Equal(3, speechRequests);
            Assert.Equal(1, player.MaximumConcurrent);
        }
        finally { await CloseAsync(window); }
    }

    [AvaloniaFact]
    public async Task StopVoiceRejectsLateAudioWithoutRemovingTheReply()
    {
        var releaseAudio = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;
        using var handler = new SpeechHttpHandler((request, _) =>
        {
            if (request.RequestUri != GeminiSpeechService.Endpoint)
                return Task.FromResult(TextResponse());
            started = true;
            return releaseAudio.Task; // Deliberately ignores cancellation to exercise late results.
        });
        var player = new SilentPlayer();
        var window = CreateWindow(handler, player);
        try
        {
            Click(window, "GenerateButton");
            await WaitAsync(() => started);
            Click(window, "StopVoiceButton");
            releaseAudio.SetResult(SpeechTestAudio.Response());
            await WaitAsync(() => !window.FindControl<Button>("StopVoiceButton")!.IsEnabled);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, player.PlayCount);
            Assert.Equal("A robot reply.", window.FindControl<TextBox>("ResponseBox")!.Text);
            Assert.True(IsStopped(window));
        }
        finally { await CloseAsync(window); }
    }

    [AvaloniaFact]
    public async Task AutoSpeakOffCancelsPlaybackAndNewGenerationDoesNotOverlap()
    {
        using var handler = new SpeechHttpHandler((request, _) => Task.FromResult(request.RequestUri == GeminiSpeechService.Endpoint ? SpeechTestAudio.Response() : TextResponse()));
        var player = new SilentPlayer { HoldPlayback = true };
        var window = CreateWindow(handler, player);
        try
        {
            Click(window, "GenerateButton");
            await WaitAsync(() => player.PlayCount == 1);
            window.FindControl<CheckBox>("AutoSpeakBox")!.IsChecked = false;
            await WaitAsync(() => player.Cancellations == 1);
            Assert.True(IsStopped(window));
            window.FindControl<CheckBox>("AutoSpeakBox")!.IsChecked = true;
            Click(window, "SpeakButton");
            await WaitAsync(() => player.PlayCount == 2);
            Click(window, "GenerateButton");
            await WaitAsync(() => player.PlayCount == 3);
            Assert.Equal(2, player.Cancellations);
            Assert.Equal(1, player.MaximumConcurrent);
        }
        finally { await CloseAsync(window); }
        Assert.Equal(3, player.Cancellations);
    }

    [AvaloniaFact]
    public async Task TurningAutoSpeakOffBeforeTheTextArrivesPreventsSpeech()
    {
        var releaseText = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SpeechHttpHandler((_, _) => releaseText.Task);
        var player = new SilentPlayer();
        var window = CreateWindow(handler, player);
        try
        {
            Click(window, "GenerateButton");
            window.FindControl<CheckBox>("AutoSpeakBox")!.IsChecked = false;
            releaseText.SetResult(TextResponse());
            await WaitAsync(() => !string.IsNullOrWhiteSpace(window.FindControl<TextBox>("ResponseBox")!.Text));
            Assert.Equal(0, player.PlayCount);
            Assert.True(IsStopped(window));
        }
        finally { await CloseAsync(window); }
    }

    [AvaloniaFact]
    public async Task SpeechErrorKeepsTheTextReplyAndAllowsReplay()
    {
        using var handler = new SpeechHttpHandler((request, _) => Task.FromResult(request.RequestUri == GeminiSpeechService.Endpoint ?
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : TextResponse()));
        var player = new SilentPlayer();
        var window = CreateWindow(handler, player);
        try
        {
            Click(window, "GenerateButton");
            await WaitAsync(() => window.FindControl<TextBlock>("SpeechError")!.IsVisible);
            Assert.Contains("high demand", window.FindControl<TextBlock>("SpeechError")!.Text);
            Assert.Equal("A robot reply.", window.FindControl<TextBox>("ResponseBox")!.Text);
            Assert.False(window.FindControl<TextBlock>("GeminiError")!.IsVisible);
            Assert.True(window.FindControl<Button>("SpeakButton")!.IsEnabled);
            Assert.Equal(0, player.PlayCount);
        }
        finally { await CloseAsync(window); }
    }

    [AvaloniaFact]
    public async Task CloseCancelsSynthesisWithoutStartingPlayback()
    {
        bool speechStarted = false;
        bool cancelled = false;
        using var handler = new SpeechHttpHandler(async (request, token) =>
        {
            if (request.RequestUri != GeminiSpeechService.Endpoint)
                return TextResponse();
            speechStarted = true;
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled = token.IsCancellationRequested; }
            return SpeechTestAudio.Response();
        });
        var player = new SilentPlayer();
        var window = CreateWindow(handler, player);
        Click(window, "GenerateButton");
        await WaitAsync(() => speechStarted);
        await CloseAsync(window);
        Assert.True(cancelled);
        Assert.Equal(0, player.PlayCount);
    }

    private static MainWindow CreateWindow(HttpMessageHandler handler, ISpeechPlayback player)
    {
        var window = new MainWindow(new HttpClient(handler), player);
        window.Show();
        window.FindControl<TextBox>("ApiKeyBox")!.Text = "fake-ui-key";
        window.FindControl<TextBox>("TranscriptBox")!.Text = "Hello.";
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static HttpResponseMessage TextResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"candidates":[{"content":{"parts":[{"text":"A robot reply."}]}}]}""")
    };

    private static void Click(MainWindow window, string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static bool IsStopped(MainWindow window) => window.FindControl<TextBlock>("SpeechStatus")!.Text == "Stopped";
    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await WaitAsync(() => !window.IsVisible);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Expected speech UI state did not arrive.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class SilentPlayer : ISpeechPlayback
    {
        public int PlayCount;
        public int Cancellations;
        public int MaximumConcurrent;
        public bool HoldPlayback;
        private int active;
        private TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Finish() => completion.TrySetResult();

        public async Task PlayAsync(byte[] wav, CancellationToken cancellationToken)
        {
            GeminiSpeechService.ValidateWav(wav);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref active));
            Interlocked.Increment(ref PlayCount);
            try
            {
                if (HoldPlayback)
                    await completion.Task.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancellations);
                throw;
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
