using Avalonia;

namespace ContentCoHostStudio;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        try
        {
            CliConsole.Initialize();
            using var cancelled = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
            Console.CancelKeyPress += onCancel;
            try
            {
                using var client = GeminiService.CreateClient();
                return new CliApplication(client, Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
                    .RunAsync(args, Console.In, Console.Out, Console.Error, cancelled.Token).GetAwaiter().GetResult();
            }
            finally { Console.CancelKeyPress -= onCancel; }
        }
        catch (Exception) { return 6; } // Never let an unhandled exception print request data or credentials.
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
