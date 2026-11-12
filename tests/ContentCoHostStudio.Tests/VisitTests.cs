using System.Text.Json;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class VisitTests
{
    [Fact]
    public async Task ExternalVisitCancellationBecomesInactiveBeforeGracefulSocketCleanupCompletes()
    {
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeLiveTransport { Closing = _ => { enteredClose.TrySetResult(); return releaseClose.Task; } };
        using var stop = new CancellationTokenSource();
        await using var session = new GeminiLiveSession(transport, new SilentLivePlayback(), new FakeLiveMicrophone());
        var run = session.RunAsync("key", LiveSessionTests.Options(), "context", stop.Token);
        await LiveTestWait.UntilAsync(() => transport.Sent.Count == 1);
        stop.Cancel();
        Assert.False(session.IsActive);
        await enteredClose.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted); releaseClose.TrySetResult(); await run;
    }

    [Fact]
    public async Task PlaybackFailureStopsTheSessionAndReleasesInputWithoutLeakingErrorData()
    {
        var transport = new FakeLiveTransport(); var mic = new FakeLiveMicrophone(); var playback = new SilentLivePlayback();
        await using var session = new GeminiLiveSession(transport, playback, mic);
        string? error = null; session.Error += message => error = message;
        var run = session.RunAsync("private-key", LiveSessionTests.Options(true), "", TestContext.Current.CancellationToken);
        transport.Push("{\"setupComplete\":{}}"); await LiveTestWait.UntilAsync(() => mic.Subscriptions == 1);
        playback.Fail(); await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(LiveSessionState.Failed, session.State); Assert.True(transport.Aborted); Assert.Equal(1, mic.Releases);
        Assert.NotNull(error); Assert.DoesNotContain("private-key", error); Assert.Equal(0, playback.BufferedBytes);
    }

    [Fact]
    public async Task ScheduleWaitsFullIntervalNeverOverlapsAndRestartsAfterCompletionOrDismissal()
    {
        var clock = new ManualVisitClock();
        var finishes = new Queue<TaskCompletionSource<bool>>();
        int calls = 0, active = 0, maximum = 0;
        var schedule = new LiveVisitSchedule(async token =>
        {
            calls++; maximum = Math.Max(maximum, ++active);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); finishes.Enqueue(done);
            try { return await done.Task.WaitAsync(token); } finally { active--; }
        }, () => TimeSpan.FromMinutes(5), clock);
        schedule.Start(); Assert.Equal(LiveVisitState.Waiting, schedule.State); Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromSeconds(299)); Assert.Equal(0, calls); Assert.Equal(TimeSpan.FromSeconds(1), schedule.Remaining);
        clock.Advance(TimeSpan.FromSeconds(1)); await LiveTestWait.UntilAsync(() => calls == 1);
        clock.Advance(TimeSpan.FromHours(1)); schedule.VisitNow(); Assert.Equal(1, calls);
        finishes.Dequeue().SetResult(true); await LiveTestWait.UntilAsync(() => schedule.State == LiveVisitState.Waiting);
        Assert.Equal(TimeSpan.FromMinutes(5), schedule.Remaining);
        schedule.VisitNow(); await LiveTestWait.UntilAsync(() => calls == 2);
        schedule.Dismiss(); await LiveTestWait.UntilAsync(() => schedule.State == LiveVisitState.Waiting);
        Assert.Equal(TimeSpan.FromMinutes(5), schedule.Remaining); Assert.Equal(1, maximum);
        await schedule.StopAsync(); clock.Advance(TimeSpan.FromDays(2)); Assert.Equal(2, calls); Assert.False(schedule.OwnsLive);
    }

    [Fact]
    public async Task FailurePausesAndResumeRequiresAnotherFullInterval()
    {
        var clock = new ManualVisitClock(); int calls = 0;
        var schedule = new LiveVisitSchedule(_ => { calls++; return Task.FromResult(false); }, () => TimeSpan.FromSeconds(5), clock);
        schedule.Start(); schedule.VisitNow(); await LiveTestWait.UntilAsync(() => schedule.State == LiveVisitState.Paused);
        clock.Advance(TimeSpan.FromDays(1)); Assert.Equal(1, calls);
        schedule.Start(); Assert.Equal(LiveVisitState.Waiting, schedule.State); Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(5)); await LiveTestWait.UntilAsync(() => calls == 2);
        await schedule.StopAsync(); Assert.Equal(LiveVisitState.Off, schedule.State);
    }

    [Fact]
    public async Task StopCancelsTheActiveVisitAndRejectsOldWaitingTimersAfterRestart()
    {
        var clock = new ManualVisitClock(); int calls = 0, cancelled = 0;
        var schedule = new LiveVisitSchedule(async token =>
        {
            calls++;
            try { await Task.Delay(Timeout.Infinite, token); } finally { cancelled++; }
            return true;
        }, () => TimeSpan.FromSeconds(10), clock);
        schedule.Start(); await schedule.StopAsync(); schedule.Start();
        clock.Advance(TimeSpan.FromSeconds(9)); Assert.Equal(0, calls);
        schedule.VisitNow(); await LiveTestWait.UntilAsync(() => calls == 1);
        await schedule.StopAsync(); Assert.Equal(1, cancelled);
        clock.Advance(TimeSpan.FromDays(1)); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(600)]
    public async Task ConfigurableDeadlineIncludesConnectionAndStopsUploadsExactlyAtExpiry(int seconds)
    {
        var clock = new ManualVisitClock(); var transport = new FakeLiveTransport(); var mic = new FakeLiveMicrophone(); var player = new SilentLivePlayback();
        await using var session = new GeminiLiveSession(transport, player, mic, clock: clock);
        Task run = session.RunAsync("key", LiveSessionTests.Options(true) with { DurationSeconds = seconds }, "", TestContext.Current.CancellationToken);
        await LiveTestWait.UntilAsync(() => transport.Sent.Count == 1);
        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        transport.Push("{\"setupComplete\":{}}"); await LiveTestWait.UntilAsync(() => mic.Subscriptions == 1);
        mic.Queue.TryWrite([1, 2]); await LiveTestWait.UntilAsync(() => transport.Sent.Count == 2);
        clock.Advance(TimeSpan.FromSeconds(1)); await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(LiveSessionState.TimedOut, session.State); Assert.False(session.IsActive); Assert.True(transport.Aborted);
        Assert.Equal(1, mic.Releases); Assert.False(mic.Queue.TryWrite([1, 2])); Assert.Equal(2, transport.Sent.Count); Assert.Equal(0, player.BufferedBytes);
    }

    [Theory]
    [InlineData(LiveReplyLength.OneWord, "exactly one spoken word")]
    [InlineData(LiveReplyLength.OneSentence, "exactly one short sentence")]
    [InlineData(LiveReplyLength.Custom, "no more than 7 spoken words")]
    public void BrevityIsSeparateAndOverridesConflictingLengthInstructions(LiveReplyLength length, string expected)
    {
        var options = LiveSessionTests.Options(true) with { ReplyLength = length, MaximumWords = 7, Behavior = "Speak at length." };
        using var document = JsonDocument.Parse(GeminiLiveSession.BuildSetup(options).ToJsonString());
        string prompt = document.RootElement.GetProperty("setup").GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains(options.Personality, prompt); Assert.Contains(options.VoiceStyle, prompt); Assert.Contains(options.Behavior, prompt);
        Assert.Contains("takes precedence", prompt); Assert.EndsWith(options.BrevityInstruction, prompt); Assert.Contains(expected, prompt);
        Assert.Equal(30, new LiveSessionOptions(true, "", "Puck", "", "").DurationSeconds);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(601, 20)]
    [InlineData(30, 0)]
    [InlineData(30, 101)]
    public void InvalidLimitsAreRejectedBeforeConnection(int seconds, int words) =>
        Assert.Throws<InvalidOperationException>(() => (LiveSessionTests.Options() with { DurationSeconds = seconds, MaximumWords = words }).Validate());

    [Fact]
    public void PlaybackVisibilityUsesHardwareRangesAndTurnCompletionNotSilenceOrSoftwareQueue()
    {
        long identity = 0; var buffer = new LivePlaybackBuffer(() => ++identity);
        buffer.Append(new byte[480]); var output = new byte[960]; buffer.Read(output, 0, output.Length);
        Assert.Equal(0, buffer.BufferedBytes); Assert.Empty(buffer.Poll(0));
        Assert.Equal(new LivePlaybackTurn(1, true), Assert.Single(buffer.Poll(2)));
        Assert.Empty(buffer.Poll(960)); // Even completely silent PCM remains visible within an unfinished turn.
        buffer.Append(new byte[480]); buffer.Read(output, 0, output.Length); buffer.CompleteTurn();
        Assert.Empty(buffer.Poll(1000)); // More audio has reached the driver but is not yet played.
        Assert.Equal(new LivePlaybackTurn(1, false), Assert.Single(buffer.Poll(1440)));
        buffer.Append(new byte[480]); buffer.CompleteTurn(); buffer.Read(output, 0, output.Length);
        Assert.Equal(new LivePlaybackTurn(2, true), Assert.Single(buffer.Poll(2000)));
        Assert.Equal(new LivePlaybackTurn(2, false), Assert.Single(buffer.Poll(2400)));
    }

    [Fact]
    public void MutedPlaybackNeverAppearsAndBuffersStayBounded()
    {
        using var playback = new LiveAudioPlayback(muted: true); var changes = new List<LivePlaybackTurn>(); playback.TurnChanged += changes.Add;
        playback.Append([1, 2]); playback.CompleteTurn(); Assert.Empty(changes); Assert.False(playback.IsSpeaking);
        var buffer = new LivePlaybackBuffer(() => 1); buffer.Append(new byte[LiveAudioPlayback.MaximumBufferedBytes]);
        Assert.Throws<LiveSessionException>(() => buffer.Append([1, 2]));
    }

    [Fact]
    public void HiddenSpoutFramesAreTransparentAndExportsRemainVisibleByDefault()
    {
        var pack = new AvatarPackLibrary().Starter;
        using var renderer = new SpoutFrameRenderer(pack);
        var visible = new AvatarOutputSnapshot("Sprites.normal_idle001.png", 0, 0, false, 0, 1);
        Assert.True(visible.Visible); renderer.Render(visible); Assert.Contains(renderer.Pixels.ToArray(), b => b != 0);
        renderer.Render(visible with { Visible = false }); Assert.All(renderer.Pixels.ToArray(), b => Assert.Equal(0, b));
        using var export = new SpoutFrameRenderer(pack, 1080, 0xFF00FF00); export.Render(visible);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, export.Pixels.Span[..4].ToArray());
    }
}

internal sealed class ManualVisitClock : TimeProvider
{
    private readonly object gate = new();
    private long now = TimeSpan.TicksPerSecond;
    private readonly List<ManualTimer> timers = [];
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return now; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state); lock (gate) timers.Add(timer); timer.Change(dueTime, period); return timer;
    }
    public void Advance(TimeSpan amount)
    {
        ManualTimer[] due;
        lock (gate)
        {
            now += amount.Ticks; due = timers.Where(t => t.Due <= now).ToArray();
            foreach (var timer in due) timer.Due = timer.Period > 0 ? now + timer.Period : long.MaxValue;
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }
    private sealed class ManualTimer(ManualVisitClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback = callback; public object? State = state; public long Due = long.MaxValue, Period;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.now + dueTime.Ticks; Period = period.Ticks; return true;
            }
        }
        public void Dispose() { lock (clock.gate) { disposed = true; clock.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
