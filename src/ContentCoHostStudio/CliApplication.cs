using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContentCoHostStudio;

public sealed class CliApplication(HttpClient client, string? apiKey, AvatarPackLibrary? library = null, INarrationExporter? exporter = null)
{
    private readonly AvatarPackLibrary library = library ?? new AvatarPackLibrary();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const int MaximumRequestCharacters = 1024 * 1024;

    public async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (args is ["--help"] or ["help"])
            {
                await WriteAsync(output, new
                {
                    schemaVersion = 1,
                    success = true,
                    commands = new[] { "narrate --request <job.json|->", "reply --request <job.json|->", "avatars list", "models list", "--help" },
                    authentication = "GEMINI_API_KEY environment variable only",
                    defaults = new NarrationJobRequest(),
                    example = new { text = "A short co-host line.", output = "cohost.webm", size = 1080 },
                    exitCodes = new { success = 0, invalidRequest = 2, dependency = 3, generation = 4, encoding = 5, unexpected = 6, cancelled = 130 }
                });
                return 0;
            }
            if (args is ["avatars", "list"])
            {
                try { await Task.Run(library.LoadInstalled, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { throw new CliException(CliExitCode.Dependency, "Could not read the avatar library. Check folder access and installed artwork."); }
                await WriteAsync(output, new
                {
                    schemaVersion = 1,
                    success = true,
                    avatars = library.Packs.Select(info => new
                    { id = info.Id, name = info.DisplayName, bundled = info.Bundled, poses = library.Find(info.Id)!.Catalog.Sets.Keys })
                });
                return 0;
            }
            if (args is ["models", "list"])
            {
                if (string.IsNullOrWhiteSpace(apiKey)) throw new CliException(CliExitCode.InvalidRequest, "Set GEMINI_API_KEY before listing account models.");
                IReadOnlyList<GeminiModelOption> models;
                try { models = await new GeminiModelCatalog(client).LoadAsync(apiKey, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { throw new CliException(CliExitCode.Generation, "Could not list account models. Check the key, network, and account access."); }
                await WriteAsync(output, new
                {
                    schemaVersion = 1,
                    success = true,
                    models = models.Where(model => model.Kind != GeminiModelKind.Live).Select(model => new
                    { id = model.Id, kind = model.Kind.ToString().ToLowerInvariant(), supported = model.IsSupported, preview = model.IsPreview, explanation = model.Explanation })
                });
                return 0;
            }
            if (args.Length != 3 || args[0] is not ("narrate" or "reply") || args[1] != "--request")
                throw new CliException(CliExitCode.InvalidRequest, "Use narrate or reply with --request <job.json|->. Use --help for commands and defaults.");
            string json;
            try
            {
                if (args[2] == "-") json = await ReadBoundedAsync(input, token).ConfigureAwait(false);
                else
                {
                    using var file = new StreamReader(args[2], new UTF8Encoding(false, true), true);
                    json = await ReadBoundedAsync(file, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new CliException(CliExitCode.InvalidRequest, "Could not read the UTF-8 job request, or it exceeded the 1 MiB character limit."); }
            NarrationJobRequest request;
            try { request = JsonSerializer.Deserialize<NarrationJobRequest>(json, Json) ?? throw new JsonException(); }
            catch (Exception) { throw new CliException(CliExitCode.InvalidRequest, "Invalid request JSON. Use the documented fields only; credentials do not belong in request JSON."); }
            request = request with { Mode = args[0] == "reply" ? NarrationMode.Reply : NarrationMode.Narrate };
            var progress = new InlineProgress<JobProgress>(value =>
            {
                lock (error) error.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, stage = value.Stage, percent = value.Percent }, Json));
            });
            var result = await new NarrationJobRunner(client, apiKey ?? "", library, exporter).RunAsync(request, token, progress).ConfigureAwait(false);
            await WriteAsync(output, new { schemaVersion = 1, success = true, result });
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { await FailureAsync(output, CliExitCode.Cancelled, "Job cancelled. Child process termination and temporary-file cleanup were requested."); return (int)CliExitCode.Cancelled; }
        catch (CliException exception)
        { await FailureAsync(output, exception.Code, exception.Message); return (int)exception.Code; }
        catch (Exception)
        { await FailureAsync(output, CliExitCode.Internal, "The command could not complete. Check dependencies, access permissions, and the request; no raw diagnostic data was printed."); return (int)CliExitCode.Internal; }
    }
    private static async Task<string> ReadBoundedAsync(TextReader input, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[4096];
        int read;
        // Console.In can wrap a synchronous reader. Isolate that read so Ctrl+C can
        // cancel while stdin remains open; the abandoned read owns its buffer.
        while ((read = await Task.Run(async () => await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false), token)
            .WaitAsync(token).ConfigureAwait(false)) > 0)
        {
            if (text.Length + read > MaximumRequestCharacters) throw new InvalidDataException();
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
    private static Task FailureAsync(TextWriter output, CliExitCode code, string message) => WriteAsync(output,
        new { schemaVersion = 1, success = false, error = new { code = code.ToString(), message } });
    private static Task WriteAsync(TextWriter writer, object value) => writer.WriteLineAsync(JsonSerializer.Serialize(value, Json));
}
