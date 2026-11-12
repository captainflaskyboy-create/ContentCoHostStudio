using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class LiveModelWindowTests
{
    private const string Alternative = "gemini-3.1-flash-live-preview";

    [AvaloniaFact]
    public async Task VisibleIndependentSelectorRoutesSessionAndKeepsLastModelAfterSelectionChanges()
    {
        var transport = new FakeLiveTransport();
        GeminiLiveSession? session = null;
        var window = new MainWindow(new HttpClient(new SpeechHttpHandler((_, _) => throw new Xunit.Sdk.XunitException("No HTTP expected."))),
            liveFactory: () => session = new(transport, new SilentLivePlayback(), new FakeLiveMicrophone()), audioDevices: new FakeAudioCatalog());
        window.Show();
        var box = window.FindControl<ComboBox>("LiveModelBox")!;
        Assert.True(box.IsEffectivelyVisible);
        Assert.Equal(GeminiLiveSession.Model, Selected(box).Id);
        Assert.False(Items(box).Single(item => ((GeminiModelOption)item.Tag!).Id.Contains("extended-thinking")).IsEnabled);
        box.SelectedItem = Items(box).Single(item => ((GeminiModelOption)item.Tag!).Id == Alternative);
        Assert.Equal(GeminiService.Model, Selected(window.FindControl<ComboBox>("TextModelBox")!).Id);
        Assert.Equal(GeminiSpeechService.Model, Selected(window.FindControl<ComboBox>("SpeechModelBox")!).Id);
        Assert.Contains("not supported", window.FindControl<TextBlock>("LiveModelHelp")!.Text);
        window.FindControl<TextBox>("ApiKeyBox")!.Text = "fake-key";
        window.FindControl<TextBox>("TranscriptBox")!.Text = "Human transcript";
        window.FindControl<CheckBox>("UseLiveBox")!.IsChecked = true;
        window.FindControl<CheckBox>("LiveMicrophoneBox")!.IsChecked = false;
        Click(window, "StartLiveButton");
        await UntilAsync(() => transport.Sent.Count == 1);
        Assert.Contains("models/" + Alternative, transport.Sent.Single());
        Assert.False(box.IsEnabled);
        Assert.Equal(Alternative, session!.SessionModel);
        transport.Push("{\"setupComplete\":{}}");
        transport.Push(LiveSessionTests.AudioEvent("Selected model reply"));
        await UntilAsync(() => window.FindControl<TextBox>("ResponseBox")!.Text!.Contains("Selected model reply"));
        Click(window, "StopLiveButton");
        await UntilAsync(() => transport.Disposed && box.IsEnabled);
        box.SelectedItem = Items(box).Single(item => ((GeminiModelOption)item.Tag!).Id == GeminiLiveSession.Model);
        Assert.Contains(Alternative, window.FindControl<TextBlock>("LiveStatus")!.Text);
        Assert.Contains(Alternative, window.FindControl<TextBlock>("ResponseHeading")!.Text);
        Assert.Contains("Selected model reply", window.FindControl<TextBox>("ResponseBox")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RefreshIncludesLiveOptionsPreservesMissingSelectionAndRetainsListOnFailure()
    {
        int calls = 0;
        using var handler = new SpeechHttpHandler((_, _) => Task.FromResult(++calls switch
        {
            1 => ModelTests.Json("""{"models":[{"name":"models/gemini-3.1-flash-live-preview","supportedGenerationMethods":["bidiGenerateContent"]},{"name":"models/gemini-9-live","supportedGenerationMethods":["bidiGenerateContent"]}]}"""),
            2 => ModelTests.Json("{\"models\":[]}"),
            _ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
        }));
        var window = new MainWindow(new HttpClient(handler), audioDevices: new FakeAudioCatalog());
        window.Show();
        window.FindControl<TextBox>("ApiKeyBox")!.Text = "fake-key";
        var box = window.FindControl<ComboBox>("LiveModelBox")!;
        Click(window, "RefreshModelsButton");
        await UntilAsync(() => window.FindControl<TextBlock>("CatalogStatus")!.Text!.StartsWith("Loaded"));
        Assert.Equal(3, Items(box).Count());
        Assert.True(Selected(box).MissingFromCatalog);
        Assert.False(Items(box).Single(item => ((GeminiModelOption)item.Tag!).Id == "gemini-9-live").IsEnabled);
        box.SelectedItem = Items(box).Single(item => ((GeminiModelOption)item.Tag!).Id == Alternative);
        Click(window, "RefreshModelsButton");
        await UntilAsync(() => window.FindControl<TextBlock>("CatalogStatus")!.Text!.StartsWith("No compatible"));
        Assert.Equal(Alternative, Selected(box).Id);
        Assert.True(Selected(box).MissingFromCatalog);
        Click(window, "RefreshModelsButton");
        await UntilAsync(() => window.FindControl<TextBlock>("CatalogStatus")!.Text!.Contains("authentication"));
        Assert.Equal(Alternative, Selected(box).Id);
        Assert.Single(Items(box));
        window.Close();
    }

    private static GeminiModelOption Selected(ComboBox box) => (GeminiModelOption)((ComboBoxItem)box.SelectedItem!).Tag!;
    private static IEnumerable<ComboBoxItem> Items(ComboBox box) => box.ItemsSource!.Cast<ComboBoxItem>();
    private static void Click(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
