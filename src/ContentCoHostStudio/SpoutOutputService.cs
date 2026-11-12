using System.Runtime.InteropServices;


namespace ContentCoHostStudio;

public interface ISpoutOutput : IAsyncDisposable
{
    bool Available { get; }
    bool Running { get; }
    string SenderName { get; }
    string Status { get; }
    event Action? Changed;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    void Publish(AvatarOutputSnapshot snapshot);
}

public sealed class SpoutOutputService : ISpoutOutput
{
    private readonly Func<ISpoutSender> createSender;
    private readonly Func<IAvatarFrameRenderer> createRenderer;
    private AvatarPack? pack;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Thread? worker;
    private TaskCompletionSource? stopped;
    private AvatarOutputSnapshot latest = new("Sprites.normal_idle001.png", 0, 0, false, 0, 0);
    private volatile bool running;
    public bool Available { get; }
    public bool Running => running;
    public string SenderName { get; private set; } = "ContentCoHostStudio";
    public string Status { get; private set; }
    public event Action? Changed;

    public SpoutOutputService(IEnumerable<string> names, Func<ISpoutSender>? createSender = null,
        Func<IAvatarFrameRenderer>? createRenderer = null, bool? available = null)
    {
        var sprites = names.ToArray();
        this.createSender = createSender ?? (() => new SpoutNativeSender());
        this.createRenderer = createRenderer ?? (() => new SpoutFrameRenderer(sprites));
        bool platform = OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        Available = available ?? (platform && File.Exists(SpoutSenderInstallation.DefaultPath));
        Status = Available ? "Spout2 output is off. Install the OBS receiver plugin separately." : platform ?
            "Spout2 sender DLL is missing. See README for local SDK setup. Preview remains available." : "Spout2 requires Windows x64. Preview remains available.";
    }
    public SpoutOutputService(AvatarPack pack, Func<ISpoutSender>? createSender = null, bool? available = null)
        : this(Array.Empty<string>(), createSender, available: available) => this.pack = pack;
    public void SetPack(AvatarPack value) => Volatile.Write(ref pack, value);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (Running) return;
            if (!Available) throw new PlatformNotSupportedException("Spout2 requires Windows x64. Use the avatar preview instead.");
            if (worker?.IsAlive == true) throw new InvalidOperationException("The previous Spout2 sender is still stopping. Restart the application before enabling it again.");
            cancellation?.Dispose(); cancellation = new();
            var token = cancellation.Token;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = stopped;
            worker = new Thread(() => Run(token, started, completed)) { IsBackground = true, Name = "ContentCoHostStudio Spout2" };
            worker.Start();
            try { await started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            catch
            {
                cancellation.Cancel();
                try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { /* A stuck graphics driver must not hold the UI indefinitely. */ }
                throw;
            }
        }
        catch (Exception ex)
        {
            Status = $"Could not enable Spout2: {ex.Message}"; Changed?.Invoke(); throw;
        }
        finally { lifecycle.Release(); }
    }

    private void Run(CancellationToken token, TaskCompletionSource started, TaskCompletionSource completed)
    {
        try
        {
            // OpenGL context, native instance, Skia resources, and cleanup stay on this thread.
            using var sender = createSender();
            var selectedPack = Volatile.Read(ref pack);
            using var initialRenderer = selectedPack == null ? createRenderer() : new SpoutFrameRenderer(selectedPack);
            IAvatarFrameRenderer renderer = initialRenderer;
            sender.Send(renderer.Render(Volatile.Read(ref latest)));
            token.ThrowIfCancellationRequested();
            SenderName = sender.Name;
            running = true; Status = "Spout2 sender is running. Select it in OBS; receiver connection is not monitored.";
            started.TrySetResult(); Changed?.Invoke();
            try
            {
                while (!token.WaitHandle.WaitOne(33))
                {
                    var requested = Volatile.Read(ref pack);
                    if (!ReferenceEquals(requested, selectedPack) && requested != null)
                    {
                        var replacement = new SpoutFrameRenderer(requested);
                        if (!ReferenceEquals(renderer, initialRenderer)) renderer.Dispose();
                        renderer = replacement; selectedPack = requested;
                    }
                    var frame = Volatile.Read(ref latest);
                    if (selectedPack != null && frame.PackRevision.Length > 0 && frame.PackRevision != selectedPack.Revision) continue;
                    sender.Send(renderer.Render(frame));
                }
            }
            finally { if (!ReferenceEquals(renderer, initialRenderer)) renderer.Dispose(); }
            running = false; Status = "Spout2 output is off.";
        }
        catch (Exception ex)
        {
            Status = $"Spout2 output failed: {ex.Message} Avatar preview and speech remain available."; running = false;
            started.TrySetException(ex);
        }
        finally { running = false; completed.TrySetResult(); Changed?.Invoke(); }
    }

    public void Publish(AvatarOutputSnapshot snapshot) => Volatile.Write(ref latest, snapshot);

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            cancellation?.Cancel();
            if (stopped != null) await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation?.Dispose(); cancellation = null; worker = null;
            running = false; Status = Available ? "Spout2 output is off." : "Spout2 requires Windows x64. Use the avatar preview instead.";
        }
        catch (TimeoutException)
        { Status = "Spout2 graphics cleanup has not finished. Restart the application before enabling it again."; }
        finally { lifecycle.Release(); Changed?.Invoke(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
