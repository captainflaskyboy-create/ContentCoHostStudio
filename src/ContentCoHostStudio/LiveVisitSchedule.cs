namespace ContentCoHostStudio;

public enum LiveVisitState { Off, Waiting, Active, Stopping, Paused }

// Owns only scheduling. Every visit owns a fresh Live session and its resources.
public sealed class LiveVisitSchedule(Func<CancellationToken, Task<bool>> visit, Func<TimeSpan> interval, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private CancellationTokenSource? lifetime, waiting, currentVisit;
    private Task loop = Task.CompletedTask;
    private long waitingSince;
    private TimeSpan waitDuration;
    private volatile LiveVisitState state;
    public LiveVisitState State => state;
    public bool OwnsLive => State != LiveVisitState.Off;
    public TimeSpan Remaining => State == LiveVisitState.Waiting
        ? TimeSpan.FromTicks(Math.Max(0, (waitDuration - clock.GetElapsedTime(waitingSince)).Ticks)) : TimeSpan.Zero;
    public event Action? Changed;

    public static void ValidateInterval(TimeSpan value)
    {
        if (value < TimeSpan.FromSeconds(1) || value > TimeSpan.FromDays(1))
            throw new InvalidOperationException("Visit interval must be between one second and 24 hours.");
    }
    public void Start()
    {
        if (!loop.IsCompleted || State is not (LiveVisitState.Off or LiveVisitState.Paused)) return;
        ValidateInterval(interval());
        lifetime?.Dispose(); lifetime = new();
        loop = RunAsync(lifetime.Token);
    }
    public void VisitNow() { if (State == LiveVisitState.Waiting) Cancel(waiting); }
    public void Dismiss()
    {
        if (State != LiveVisitState.Active) return;
        SetState(LiveVisitState.Stopping); Cancel(currentVisit);
    }
    public async Task StopAsync()
    {
        if (State == LiveVisitState.Off) return;
        SetState(LiveVisitState.Stopping);
        Cancel(lifetime); Cancel(currentVisit);
        await loop;
        lifetime?.Dispose(); lifetime = null;
        SetState(LiveVisitState.Off);
    }
    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                waitDuration = interval(); ValidateInterval(waitDuration);
                waitingSince = clock.GetTimestamp();
                using (waiting = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    SetState(LiveVisitState.Waiting);
                    try { await Task.Delay(waitDuration, clock, waiting.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    finally { waiting = null; }
                }
                token.ThrowIfCancellationRequested();
                using (currentVisit = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    SetState(LiveVisitState.Active);
                    bool succeeded;
                    try { succeeded = await visit(currentVisit.Token); }
                    catch (OperationCanceledException) when (currentVisit.IsCancellationRequested) { succeeded = true; }
                    finally { currentVisit = null; }
                    token.ThrowIfCancellationRequested();
                    if (!succeeded) { SetState(LiveVisitState.Paused); return; }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { SetState(LiveVisitState.Paused); }
    }
    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { /* The completed wait/visit already released it. */ }
    }
    private void SetState(LiveVisitState value) { state = value; Changed?.Invoke(); }
}
