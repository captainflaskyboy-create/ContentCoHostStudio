namespace ContentCoHostStudio;

// Only the UI thread changes this state. Workers send results tagged with a generation.
public sealed class TranscriptState
{
    public string SavedText { get; set; } = "";
    public string PartialText { get; private set; } = "";
    public int Generation { get; private set; }

    public void Apply(int generation, string text, bool isFinal)
    {
        if (generation != Generation)
            return;

        if (!isFinal)
        {
            PartialText = text;
            return;
        }

        PartialText = "";
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Repeated sentences may be intentional; never deduplicate by text.
            string separator = SavedText.Length == 0 || SavedText.EndsWith('\n') ? "" : Environment.NewLine;
            SavedText += separator + text.Trim();
        }
    }

    public int Clear()
    {
        SavedText = "";
        PartialText = "";
        return ++Generation;
    }

    public void ClearPartial() => PartialText = "";
}
