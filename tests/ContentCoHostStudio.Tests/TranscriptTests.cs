using Xunit;

namespace ContentCoHostStudio.Tests;

public class TranscriptTests
{
    [Fact]
    public void PartialResultsReplaceEachOtherWithoutAppendingWords()
    {
        var state = new TranscriptState();
        state.Apply(0, "hello", false);
        state.Apply(0, "hello world", false);
        Assert.Equal("hello world", state.PartialText);
        Assert.Empty(state.SavedText);
        state.Apply(0, "hello world", true);
        Assert.Empty(state.PartialText);
        Assert.Equal("hello world", state.SavedText);
    }

    [Fact]
    public void FinalResultsPreserveEditsAndIntentionalRepetition()
    {
        var state = new TranscriptState { SavedText = "Manually edited." };
        state.Apply(0, "hello", true);
        state.Apply(0, "hello", true);
        state.Apply(0, "  ", true);
        Assert.Equal(string.Join(Environment.NewLine, "Manually edited.", "hello", "hello"), state.SavedText);
    }

    [Fact]
    public void ClearRejectsQueuedPartialsAndFinalsButAcceptsNewSpeech()
    {
        var state = new TranscriptState();
        state.Apply(0, "old", false);
        int generation = state.Clear();
        state.Apply(0, "old partial", false);
        state.Apply(0, "old final", true);
        Assert.Empty(state.PartialText);
        Assert.Empty(state.SavedText);
        state.Apply(generation, "new speech", true);
        Assert.Equal("new speech", state.SavedText);
    }

    [Fact]
    public void EmptyStopFinalDoesNotDuplicateThePreviousSegment()
    {
        var state = new TranscriptState();
        state.Apply(0, "finished sentence", true);
        state.Apply(0, "", true);
        Assert.Equal("finished sentence", state.SavedText);
    }

    [Fact]
    public void StopFinalCommitsRemainingPartialAsOneSegment()
    {
        var state = new TranscriptState();
        state.Apply(0, "unfinished", false);
        state.Apply(0, "unfinished sentence", true);
        Assert.Equal("unfinished sentence", state.SavedText);
        Assert.Empty(state.PartialText);
    }
}
