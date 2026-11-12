using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ContentCoHostStudio.Tests.TestAppBuilder))]

namespace ContentCoHostStudio.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class WindowTests
{
    [AvaloniaFact]
    public void WindowOpensWithMaskedKeyAndExpectedDefaultState()
    {
        var window = new MainWindow();
        window.Show();
        Assert.Equal('●', window.FindControl<TextBox>("ApiKeyBox")!.PasswordChar);
        Assert.Contains("sarcastic", window.FindControl<TextBox>("PersonalityBox")!.Text);
        Assert.Equal("Stopped", window.FindControl<TextBlock>("ListeningStatus")!.Text);
        Assert.False(window.FindControl<Button>("GenerateButton")!.IsEnabled);
        Assert.True(window.FindControl<TextBox>("ResponseBox")!.IsReadOnly);
        window.Close();
    }

    [AvaloniaFact]
    public void ManualTranscriptEnablesGenerationAndClearDisablesIt()
    {
        var window = new MainWindow();
        window.Show();
        var key = window.FindControl<TextBox>("ApiKeyBox")!;
        var transcript = window.FindControl<TextBox>("TranscriptBox")!;
        var generate = window.FindControl<Button>("GenerateButton")!;
        key.Text = "test-key-never-sent";
        Dispatcher.UIThread.RunJobs();
        Assert.False(generate.IsEnabled);
        transcript.Text = "A manually entered transcript.";
        Dispatcher.UIThread.RunJobs();
        Assert.True(generate.IsEnabled);
        window.FindControl<Button>("ClearButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(transcript.Text ?? "");
        Assert.False(generate.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task GenerateDisplaysReplyAndKeepsEditsSeparateFromTheRequest()
    {
        var releaseResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? sentBody = null;
        using var handler = new WindowHttpHandler(async (request, token) =>
        {
            sentBody = await request.Content!.ReadAsStringAsync(token);
            return await releaseResponse.Task.WaitAsync(token);
        });
        var window = new MainWindow(new HttpClient(handler));
        window.FindControl<CheckBox>("AutoSpeakBox")!.IsChecked = false;
        window.Show();
        try
        {
            window.FindControl<TextBox>("ApiKeyBox")!.Text = "fake-ui-test-key";
            window.FindControl<TextBox>("PersonalityBox")!.Text = "A friendly robot.";
            var transcript = window.FindControl<TextBox>("TranscriptBox")!;
            transcript.Text = "Original saved transcript.";
            Dispatcher.UIThread.RunJobs();
            var generate = window.FindControl<Button>("GenerateButton")!;
            generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => sentBody != null);
            Assert.False(generate.IsEnabled);
            Assert.Equal("Generating…", window.FindControl<TextBlock>("GenerationStatus")!.Text);
            Assert.True(window.FindControl<Button>("ListenButton")!.IsEnabled);
            Assert.True(transcript.IsEnabled);
            transcript.Text = "Edited during generation.";

            using var payload = JsonDocument.Parse(sentBody!);
            Assert.Equal("A friendly robot.", payload.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal("Original saved transcript.", payload.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
            releaseResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"candidates":[{"content":{"parts":[{"text":"Beep boop, hello creator!"}]}}]}""")
            });
            await WaitUntilAsync(() => window.FindControl<TextBox>("ResponseBox")!.Text == "Beep boop, hello creator!");
            Assert.False(window.FindControl<TextBlock>("GeminiError")!.IsVisible);
            Assert.Equal("Edited during generation.", transcript.Text);
            Assert.True(generate.IsEnabled);
            Assert.Contains(GeminiService.Model, window.FindControl<TextBlock>("ResponseHeading")!.Text);
        }
        finally
        {
            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
        }
    }

    [AvaloniaFact]
    public async Task BusyServiceDisplaysReadableErrorAndAllowsAnotherRequest()
    {
        using var handler = new WindowHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var window = new MainWindow(new HttpClient(handler));
        window.Show();
        try
        {
            window.FindControl<TextBox>("ApiKeyBox")!.Text = "fake-ui-test-key";
            window.FindControl<TextBox>("TranscriptBox")!.Text = "Hello.";
            Dispatcher.UIThread.RunJobs();
            var generate = window.FindControl<Button>("GenerateButton")!;
            generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => window.FindControl<TextBlock>("GeminiError")!.IsVisible);
            Assert.Contains("high demand", window.FindControl<TextBlock>("GeminiError")!.Text);
            Assert.True(generate.IsEnabled);
            Assert.Empty(window.FindControl<TextBlock>("GenerationStatus")!.Text ?? "");
        }
        finally
        {
            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
        }
    }

    [AvaloniaFact]
    public async Task ClosingCancelsAnOutstandingRequestAndClearsTheKey()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        using var handler = new WindowHttpHandler(async (_, token) =>
        {
            requestStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                cancelled = token.IsCancellationRequested;
            }
        });
        var window = new MainWindow(new HttpClient(handler));
        window.Show();
        var key = window.FindControl<TextBox>("ApiKeyBox")!;
        key.Text = "fake-ui-test-key";
        window.FindControl<TextBox>("TranscriptBox")!.Text = "Hello.";
        Dispatcher.UIThread.RunJobs();
        window.FindControl<Button>("GenerateButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible);
        Assert.True(cancelled);
        Assert.Empty(key.Text ?? "");
    }

    [AvaloniaFact]
    [Trait("Category", "LiveGemini")]
    public async Task LiveGeminiReplyAppearsInThePocWindow()
    {
        // This is deliberately opt-in: a normal test run must never spend API quota.
        if (Environment.GetEnvironmentVariable("CONTENT_COHOST_RUN_LIVE_TEST") != "1")
            Assert.Skip("Set CONTENT_COHOST_RUN_LIVE_TEST=1 and GEMINI_API_KEY to run this paid API check.");
        string? key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            Assert.Skip("GEMINI_API_KEY is required for the opt-in live check.");

        var window = new MainWindow();
        window.Show();
        try
        {
            window.FindControl<CheckBox>("AutoSpeakBox")!.IsChecked = false;
            window.FindControl<TextBox>("ApiKeyBox")!.Text = key;
            window.FindControl<TextBox>("PersonalityBox")!.Text = "You are a friendly robotic co-host. Reply in one short sentence.";
            window.FindControl<TextBox>("TranscriptBox")!.Text = "Say hello to a YouTube creator.";
            Dispatcher.UIThread.RunJobs();
            var generate = window.FindControl<Button>("GenerateButton")!;
            Assert.True(generate.IsEnabled);
            generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var response = window.FindControl<TextBox>("ResponseBox")!;
            var error = window.FindControl<TextBlock>("GeminiError")!;
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(response.Text) || error.IsVisible, TimeSpan.FromSeconds(70));
            Assert.False(error.IsVisible, error.Text);
            // Do not include the live reply or key in assertion output.
            Assert.True(!string.IsNullOrWhiteSpace(response.Text), "The POC did not display a reply.");
            Assert.True(generate.IsEnabled);
        }
        finally
        {
            window.Close();
            await WaitUntilAsync(() => !window.IsVisible);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The expected UI state did not arrive before the timeout.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class WindowHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
