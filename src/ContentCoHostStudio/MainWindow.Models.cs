using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ContentCoHostStudio;

public partial class MainWindow
{
    private string selectedTextModel = GeminiService.Model;
    private string selectedSpeechModel = GeminiSpeechService.Model;
    private string selectedLiveModel = GeminiLiveSession.Model;
    private bool updatingModelItems;
    private bool refreshingModels;
    private int catalogRequest;
    private CancellationTokenSource? catalogSource;
    private readonly List<Task> catalogTasks = [];
    private readonly OperationTiming textTiming = new();
    private readonly OperationTiming voiceTiming = new();
    private readonly DispatcherTimer timingTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void InitializeModelControls()
    {
        updatingModelItems = true;
        SetModelItems(TextModelBox, [new(selectedTextModel, GeminiModelKind.Text, true)], selectedTextModel);
        SetModelItems(SpeechModelBox, [new(selectedSpeechModel, GeminiModelKind.Speech, true)], selectedSpeechModel);
        // Documented starting options; Refresh replaces these with the key's catalog.
        SetModelItems(LiveModelBox, new[] { GeminiLiveSession.Model, "gemini-3.1-flash-live-preview", "gemini-2.5-flash-native-audio-preview-12-2025", "gemini-3.8-live-extended-thinking" }
            .Select(id => new GeminiModelOption(id, GeminiModelKind.Live, GeminiModelCatalog.GetLiveProtocol(id) != LiveProtocol.Unsupported,
                GeminiModelCatalog.LiveExplanation(id))), selectedLiveModel);
        updatingModelItems = false;
        timingTimer.Tick += (_, _) => { UpdateTimingLabels(); UpdateLiveControls(); };
        timingTimer.Start();
        UpdateTimingLabels();
        UpdateRefreshButton();
    }

    private static GeminiModelOption? SelectedModel(ComboBox? box) => (box?.SelectedItem as ComboBoxItem)?.Tag as GeminiModelOption;

    private static void SetModelItems(ComboBox box, IEnumerable<GeminiModelOption> options, string selectedId)
    {
        var items = options.OrderBy(option => option.Id, StringComparer.Ordinal).Select(option =>
        {
            var item = new ComboBoxItem { Content = option.Label, Tag = option, IsEnabled = option.IsSupported };
            ToolTip.SetTip(item, option.Explanation ?? (option.MissingFromCatalog ? "This selection was not returned by the latest refresh. Access is not guaranteed." : option.Id));
            ToolTip.SetShowOnDisabled(item, true);
            return item;
        }).ToArray();
        box.ItemsSource = items;
        box.SelectedItem = items.FirstOrDefault(item => ((GeminiModelOption)item.Tag!).Id == selectedId);
    }

    private void TextModelChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!updatingModelItems && SelectedModel(TextModelBox) is { IsSupported: true } option)
            selectedTextModel = option.Id;
        UpdateGenerateButton();
    }

    private void LiveModelChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!updatingModelItems && liveSession?.IsActive != true && liveTask.IsCompleted && SelectedModel(LiveModelBox) is { IsSupported: true } option)
            selectedLiveModel = option.Id;
        UpdateLiveControls();
    }

    private void SpeechModelChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!updatingModelItems && SelectedModel(SpeechModelBox) is { IsSupported: true } option && option.Id != selectedSpeechModel)
        {
            selectedSpeechModel = option.Id;
            StopVoice();
        }
        UpdateSpeechControls();
    }

    private void ApiKeyChanged(object? sender, TextChangedEventArgs args)
    {
        ++catalogRequest;
        catalogSource?.Cancel();
        refreshingModels = false;
        if (CatalogStatus is not null)
            CatalogStatus.Text = "Models unverified for this key. Click Refresh Models.";
        UpdateRefreshButton();
        UpdateGenerateButton();
    }

    private void UpdateRefreshButton()
    {
        if (RefreshModelsButton is not null && ApiKeyBox is not null)
            RefreshModelsButton.IsEnabled = !closing && !refreshingModels && !string.IsNullOrWhiteSpace(ApiKeyBox.Text);
    }

    private async void RefreshModelsClicked(object? sender, RoutedEventArgs args)
    {
        if (closing || refreshingModels)
            return;
        catalogSource?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(closingSource.Token);
        catalogSource = source;
        int requestId = ++catalogRequest;
        refreshingModels = true;
        CatalogStatus.Text = "Refreshing model lists…";
        UpdateRefreshButton();
        catalogTasks.RemoveAll(task => task.IsCompleted);
        Task task = RefreshModelsAsync(ApiKeyBox.Text ?? "", requestId, source);
        catalogTasks.Add(task);
        await task;
    }

    private async Task RefreshModelsAsync(string key, int requestId, CancellationTokenSource source)
    {
        try
        {
            var options = await new GeminiModelCatalog(httpClient).LoadAsync(key, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (closing || requestId != catalogRequest)
                return;
            var texts = options.Where(option => option.Kind == GeminiModelKind.Text).ToList();
            var speeches = options.Where(option => option.Kind == GeminiModelKind.Speech).ToList();
            var lives = options.Where(option => option.Kind == GeminiModelKind.Live).ToList();
            PreserveSelection(texts, SelectedModel(TextModelBox));
            PreserveSelection(speeches, SelectedModel(SpeechModelBox));
            PreserveSelection(lives, SelectedModel(LiveModelBox));
            updatingModelItems = true;
            try
            {
                SetModelItems(TextModelBox, texts, selectedTextModel);
                SetModelItems(SpeechModelBox, speeches, selectedSpeechModel);
                SetModelItems(LiveModelBox, lives, selectedLiveModel);
            }
            finally { updatingModelItems = false; }
            CatalogStatus.Text = options.Count == 0 ? "No compatible Gemini models returned. Existing selections retained." :
                $"Loaded {options.Count(option => option.Kind == GeminiModelKind.Text)} text, {options.Count(option => option.Kind == GeminiModelKind.Speech)} speech, and {options.Count(option => option.Kind == GeminiModelKind.Live)} Live models. Catalog listing does not guarantee generation access.";
            UpdateGenerateButton();
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!closing && requestId == catalogRequest)
                CatalogStatus.Text = exception is InvalidOperationException ? exception.Message : "Could not refresh models. The previous lists were retained.";
        }
        finally
        {
            if (ReferenceEquals(catalogSource, source))
                catalogSource = null;
            source.Dispose();
            if (requestId == catalogRequest)
            {
                refreshingModels = false;
                UpdateRefreshButton();
            }
        }
    }

    private static void PreserveSelection(List<GeminiModelOption> options, GeminiModelOption? selection)
    {
        if (selection != null && options.All(option => option.Id != selection.Id))
            options.Add(selection with { MissingFromCatalog = true });
    }

    private void UpdateTimingLabels()
    {
        if (TextTimingLabel is not null)
            TextTimingLabel.Text = textTiming.Describe("Text generation");
        if (VoiceTimingLabel is not null)
            VoiceTimingLabel.Text = voiceTiming.Describe("Voice preparation");
    }
}
