using System.Diagnostics;

namespace ContentCoHostStudio;

// Stopwatch is monotonic: wall-clock changes cannot distort the measured duration.
public sealed class OperationTiming
{
    private readonly Stopwatch stopwatch = new();
    public string Outcome { get; private set; } = "Not requested";
    public TimeSpan Elapsed => stopwatch.Elapsed;

    public void Start()
    {
        stopwatch.Restart();
        Outcome = "Running";
    }

    public void Finish(string outcome)
    {
        if (Outcome != "Running")
            return;
        stopwatch.Stop();
        Outcome = outcome;
    }

    public void Cached()
    {
        stopwatch.Reset();
        Outcome = "Cached";
    }

    public string Describe(string stage) => Outcome switch
    {
        "Not requested" => $"{stage}: not requested",
        "Cached" => $"{stage}: cached audio (no API request)",
        _ => $"{stage}: {Elapsed.TotalSeconds:0.0} s · {Outcome.ToLowerInvariant()}"
    };
}
