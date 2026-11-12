using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class ModelWindowTests
{
    private const string FastText = "gemini-3.5-flash-lite";
    private const string FastSpeech = "gemini-3.8-flash-lite-tts";
    private const string LegacySpeech = "gemini-3.1-flash-tts-preview";

    [AvaloniaFact]
    public async Task RefreshPreservesSelectionsMarksMissingModelsAndRetainsListsOnFailure()
    {
        int calls = 0;
        using var handler = new SpeechHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(++calls switch
            {
                1 => Catalog(),
                2 => ModelTests.Json("{\"models\":[]}"),
                _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            });
        });
        var window = CreateWindow(handler, new ModelPlayer());
        try
        {
            Assert.Equal(GeminiService.Model, Selected(window, "TextModelBox").Id);
            Assert.Equal(GeminiSpeechService.Model, Selected(window, "SpeechModelBox").Id);
            Click(window, "RefreshModelsButton");
            await WaitAsync(() => Status(window).StartsWith("Loaded"));
            var unsupported = Items(window, "SpeechModelBox").Single(item => ((GeminiModelOption)item.Tag!).Id == "gemini-9-flash-tts");
            Assert.False(unsupported.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(unsupported)?.ToString()));
            Assert.Contains(Items(window, "TextModelBox"), item => item.Content!.ToString()!.Contains("preview/experimental"));
            Select(window, "TextModelBox", FastText);
            Select(window, "SpeechModelBox", FastSpeech);
            Click(window, "RefreshModelsButton");
            await WaitAsync(() => Status(window).StartsWith("No compatible"));
            Assert.True(Selected(window, "TextModelBox").MissingFromCatalog);
            Assert.True(Selected(window, "SpeechModelBox").MissingFromCatalog);
            Assert.Equal(FastText, Selected(window, "TextModelBox").Id);
            Assert.Equal(FastSpeech, Selected(window, "SpeechModelBox").Id);
            Click(window, "RefreshModelsButton");
            await WaitAsync(() => Status(window).Contains("authentication"));
            Assert.Equal(FastText, Selected(window, "TextModelBox").Id);
            Assert.Equal(FastSpeech, Selected(window, "SpeechModelBox").Id);
            Assert.Equal(3, calls);
            Assert.True(window.FindControl<Button>("ListenButton")!.IsEnabled);
        }
        finally { await CloseAsync(window); }
    }

    [AvaloniaFact]
    public async Task KeyChangesCancelRefreshAndOldResultsCannotReplaceNewCatalog()
    {
        var oldResult = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        bool oldCancelled = false;
        using var handler = new SpeechHttpHandler((_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                token.Register(() => oldCancelled = true);
                return oldResult.Task;
            }
            return Task.FromResult(Catalog());
        });
        var window = CreateWindow(handler, new ModelPlayer());
        try
        {
            Click(window, "RefreshModelsButton");
            await WaitAsync(() => calls == 1);
            window.FindControl<TextBox>("ApiKeyBox")!.Text = "second-fake-key";
            Dispatcher.UIThread.RunJobs();
            Assert.True(oldCancelled);
            Assert.Contains("unverified", Status(window));
            Assert.True(window.FindControl<Button>("RefreshModelsButton")!.IsEnabled);
            Click(window, "RefreshModelsButton");
            await WaitAsync(() => Status(window).StartsWith("Loaded"));
            oldResult.SetResult(ModelTests.Json("""{"models":[{"name":"models/gemini-old-only","supportedGenerationMethods":["generateContent"]}]}"""));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(Items(window, "TextModelBox"), item => ((GeminiModelOption)item.Tag!).Id == "gemini-old-only");
            Assert.Contains(Items(window, "TextModelBox"), item => ((GeminiModelOption)item.Tag!).Id == FastText);
        }
        finally
        {
            oldResult.TrySetResult(Catalog());
            await CloseAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RequestsSnapshotSelectionsAndSpeechModelChangesInvalidateCacheAndRejectLateAudio()
    {
        var textResult = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldAudio = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? requestedText = null;
        var speechModels = new List<string>();
        using var handler = new SpeechHttpHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return Catalog();
            if (request.RequestUri == GeminiSpeechService.Endpoint)
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                string model = document.RootElement.GetProperty("model").GetString()!;
                speechModels.Add(model);
                return speechModels.Count == 1 ? await oldAudio.Task : SpeechTestAudio.Response();
            }
            if (request.RequestUri!.AbsolutePath.Contains("tts"))
            {
                speechModels.Add(LegacySpeech);
                return ModelTests.LegacyResponse(new byte[2400], "audio/L16;rate=24000");
            }
            requestedText = request.RequestUri.AbsolutePath;
            return await textResult.Task;
        });
        var player = new ModelPlayer();
        var window = CreateWindow(handler, player);
        window.FindControl<CheckBox>("AutoSpeakBox")!.IsChecked = false;
        try
        {
            Click(window, "RefreshModelsButton");
            await WaitAsync(() => Status(window).StartsWith("Loaded"));
            Select(window, "TextModelBox", FastText);
            Click(window, "GenerateButton");
            await WaitAsync(() => requestedText != null);
            Assert.Contains(FastText, requestedText);
            Select(window, "TextModelBox", GeminiService.Model);
            Assert.Contains("running", window.FindControl<TextBlock>("TextTimingLabel")!.Text);
            textResult.SetResult(TextResponse());
            await WaitAsync(() => window.FindControl<TextBlock>("TextTimingLabel")!.Text!.Contains("completed"));
            Assert.Contains(FastText, window.FindControl<TextBlock>("LastTextModel")!.Text);
            Assert.Equal(GeminiService.Model, Selected(window, "TextModelBox").Id);

            Select(window, "SpeechModelBox", FastSpeech);
            Click(window, "SpeakButton");
            await WaitAsync(() => speechModels.Count == 1);
            Select(window, "SpeechModelBox", LegacySpeech);
            Assert.Contains("cancelled", window.FindControl<TextBlock>("VoiceTimingLabel")!.Text);
            oldAudio.SetResult(SpeechTestAudio.Response());
            Click(window, "SpeakButton");
            await WaitAsync(() => player.Plays == 1 && Stopped(window));
            Assert.Equal(new[] { FastSpeech, LegacySpeech }, speechModels);
            Assert.Contains(LegacySpeech, window.FindControl<TextBlock>("LastSpeechModel")!.Text);
            Assert.Contains("completed", window.FindControl<TextBlock>("VoiceTimingLabel")!.Text);
            Click(window, "SpeakButton");
            await WaitAsync(() => player.Plays == 2 && Stopped(window));
            Assert.Contains("cached audio", window.FindControl<TextBlock>("VoiceTimingLabel")!.Text);
            Assert.Equal(2, speechModels.Count);
            Select(window, "SpeechModelBox", GeminiSpeechService.Model);
            Click(window, "SpeakButton");
            await WaitAsync(() => player.Plays == 3 && Stopped(window));
            Assert.Equal(3, speechModels.Count);
            Assert.Contains(GeminiSpeechService.Model, window.FindControl<TextBlock>("LastSpeechModel")!.Text);
        }
        finally
        {
            textResult.TrySetResult(TextResponse());
            oldAudio.TrySetResult(SpeechTestAudio.Response());
            await CloseAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ChangingSpeechModelStopsActivePlaybackAndFailedStagesRetainTheirTimings()
    {
        int textCalls = 0;
        bool failSpeech = false;
        using var handler = new SpeechHttpHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Get ? Catalog() :
            request.RequestUri == GeminiSpeechService.Endpoint ? (failSpeech ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : SpeechTestAudio.Response()) :
            Interlocked.Increment(ref textCalls) == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : TextResponse()));
        var player = new ModelPlayer { Hold = true };
        var window = CreateWindow(handler, player);
        try
        {
            Click(window, "RefreshModelsButton");
            await WaitAsync(() => Status(window).StartsWith("Loaded"));
            Click(window, "GenerateButton");
            await WaitAsync(() => window.FindControl<TextBlock>("TextTimingLabel")!.Text!.Contains("failed"));
            Click(window, "GenerateButton");
            await WaitAsync(() => player.Plays == 1);
            Select(window, "SpeechModelBox", FastSpeech);
            await WaitAsync(() => player.Cancellations == 1);
            Assert.True(Stopped(window));
            failSpeech = true;
            Click(window, "SpeakButton");
            await WaitAsync(() => window.FindControl<TextBlock>("VoiceTimingLabel")!.Text!.Contains("failed"));
            Assert.Equal("A model selection test reply.", window.FindControl<TextBox>("ResponseBox")!.Text);
            Assert.Contains("completed", window.FindControl<TextBlock>("TextTimingLabel")!.Text);
        }
        finally { await CloseAsync(window); }
    }

    [AvaloniaFact]
    public async Task ClosingCancelsAnOutstandingCatalogRefresh()
    {
        bool started = false;
        bool cancelled = false;
        using var handler = new SpeechHttpHandler(async (_, token) =>
        {
            started = true;
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled = token.IsCancellationRequested; }
            return Catalog();
        });
        var window = CreateWindow(handler, new ModelPlayer());
        Click(window, "RefreshModelsButton");
        await WaitAsync(() => started);
        await CloseAsync(window);
        Assert.True(cancelled);
        Assert.Empty(window.FindControl<TextBox>("ApiKeyBox")!.Text ?? "");
    }

    [AvaloniaFact]
    [Trait("Category", "LiveModels")]
    public async Task LiveSelectionsCompareTwoAvailableTextAndSpeechModels()
    {
        if (Environment.GetEnvironmentVariable("CONTENT_COHOST_RUN_LIVE_MODEL_TEST") != "1")
            Assert.Skip("Set CONTENT_COHOST_RUN_LIVE_MODEL_TEST=1 and GEMINI_API_KEY for paid live model checks.");
        string? key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            Assert.Skip("GEMINI_API_KEY is required.");
        var player = new ModelPlayer();
        var window = new MainWindow(GeminiService.CreateClient(), player);
        window.Show();
        try
        {
            window.FindControl<CheckBox>("AutoSpeakBox")!.IsChecked = false;
            window.FindControl<TextBox>("ApiKeyBox")!.Text = key;
            window.FindControl<TextBox>("PersonalityBox")!.Text = "Reply in one short friendly sentence, under 12 words.";
            window.FindControl<TextBox>("TranscriptBox")!.Text = "Say hello to a YouTube creator.";
            Dispatcher.UIThread.RunJobs();
            Click(window, "RefreshModelsButton");
            await WaitAsync(() => !Status(window).StartsWith("Refreshing"), 70);
            Assert.True(Status(window).StartsWith("Loaded"), Status(window));
            string[] texts = ChooseTwo(window, "TextModelBox", GeminiService.Model, "CONTENT_COHOST_LIVE_TEXT_MODELS");
            string[] speeches = ChooseTwo(window, "SpeechModelBox", GeminiSpeechService.Model, "CONTENT_COHOST_LIVE_SPEECH_MODELS");
            Assert.Equal(2, texts.Length);
            Assert.Equal(2, speeches.Length);
            foreach (string id in texts)
            {
                Select(window, "TextModelBox", id);
                Click(window, "GenerateButton");
                await WaitAsync(() => window.FindControl<Button>("GenerateButton")!.IsEnabled, 70);
                Assert.False(window.FindControl<TextBlock>("GeminiError")!.IsVisible, id + ": " + window.FindControl<TextBlock>("GeminiError")!.Text);
                Assert.Contains(id, window.FindControl<TextBlock>("LastTextModel")!.Text);
                Assert.Contains("completed", window.FindControl<TextBlock>("TextTimingLabel")!.Text);
            }
            foreach (string id in speeches)
            {
                Select(window, "SpeechModelBox", id);
                int previousPlays = player.Plays;
                Click(window, "SpeakButton");
                await WaitAsync(() => player.Plays > previousPlays || window.FindControl<TextBlock>("SpeechError")!.IsVisible, 70);
                Assert.False(window.FindControl<TextBlock>("SpeechError")!.IsVisible, id + ": " + window.FindControl<TextBlock>("SpeechError")!.Text);
                Assert.Contains(id, window.FindControl<TextBlock>("LastSpeechModel")!.Text);
                Assert.Contains("completed", window.FindControl<TextBlock>("VoiceTimingLabel")!.Text);
            }
        }
        finally { await CloseAsync(window); }
    }

    private static string[] ChooseTwo(MainWindow window, string box, string original, string environmentVariable)
    {
        var available = Items(window, box).Select(item => (GeminiModelOption)item.Tag!)
            .Where(option => option.IsSupported && !option.MissingFromCatalog).ToArray();
        string? requested = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string[] ids = requested.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            Assert.True(ids.Length == 2 && ids.All(id => available.Any(option => option.Id == id)), "The explicit live test choices must be two supported catalog-listed identifiers.");
            return ids;
        }
        return available.OrderBy(option => option.Id == original ? 0 : option.Id.Contains("flash-lite", StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(option => option.IsPreview).ThenBy(option => option.Id, StringComparer.Ordinal).Take(2).Select(option => option.Id).ToArray();
    }

    private static MainWindow CreateWindow(HttpMessageHandler handler, ISpeechPlayback player)
    {
        var window = new MainWindow(new HttpClient(handler), player);
        window.Show();
        window.FindControl<TextBox>("ApiKeyBox")!.Text = "fake-model-key";
        window.FindControl<TextBox>("TranscriptBox")!.Text = "Hello.";
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    internal static HttpResponseMessage Catalog() => ModelTests.Json("""
        {"models":[
          {"name":"models/gemini-3.6-flash","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-3.5-flash-lite","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-3-flash-preview","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-3.8-flash-tts","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-3.8-flash-lite-tts","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-3.1-flash-tts-preview","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-9-flash-tts","supportedGenerationMethods":["generateContent"]}
        ]}
        """);

    private static HttpResponseMessage TextResponse() => ModelTests.Json("""{"candidates":[{"content":{"parts":[{"text":"A model selection test reply."}]}}]}""");
    private static string Status(MainWindow window) => window.FindControl<TextBlock>("CatalogStatus")!.Text ?? "";
    private static bool Stopped(MainWindow window) => window.FindControl<TextBlock>("SpeechStatus")!.Text == "Stopped";
    private static ComboBoxItem[] Items(MainWindow window, string name) => window.FindControl<ComboBox>(name)!.ItemsSource!.Cast<ComboBoxItem>().ToArray();
    private static GeminiModelOption Selected(MainWindow window, string name) => (GeminiModelOption)((ComboBoxItem)window.FindControl<ComboBox>(name)!.SelectedItem!).Tag!;
    private static void Select(MainWindow window, string name, string id) => window.FindControl<ComboBox>(name)!.SelectedItem = Items(window, name).Single(item => ((GeminiModelOption)item.Tag!).Id == id);
    private static void Click(MainWindow window, string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task CloseAsync(MainWindow window)
    {
        window.Close();
        await WaitAsync(() => !window.IsVisible);
    }

    private static async Task WaitAsync(Func<bool> condition, int seconds = 10)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Expected model UI state did not arrive.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class ModelPlayer : ISpeechPlayback
    {
        public int Plays;
        public int Cancellations;
        public bool Hold;
        public async Task PlayAsync(byte[] wav, CancellationToken cancellationToken)
        {
            GeminiSpeechService.ValidateWav(wav);
            Interlocked.Increment(ref Plays);
            if (Hold)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Interlocked.Increment(ref Cancellations); throw; }
            }
        }
    }
}
