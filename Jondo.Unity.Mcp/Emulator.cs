using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jondo.Unity.Launcher;
using ModelContextProtocol;

namespace Jondo.Unity.Mcp;

/// <summary>
/// The emulator's server as the tools see it: its control API on 8888, the /api/test routes behind
/// the run's key, and the server process when it was started from here.
/// </summary>
/// <remarks>
/// Two folders, which are the same one unless said otherwise:
///   * the data root (<see cref="Paths.Root"/>, JONDO_EMULATOR_ROOT): the databases, the content,
///     the logs -- what a server reads;
///   * the source (JONDO_MCP_SOURCE): the checkout used for the server build and legacy bundled client mods.
///     A worktree can run against the main checkout's data.
///
/// The test routes only exist on a server started with JONDO_TEST_KEY, and every call carries it.
/// The key is JONDO_TEST_KEY's when the tools are given one -- a server started by hand with the same
/// key is then usable -- and one drawn for this run otherwise.
/// </remarks>
public sealed class Emulator : IDisposable
{
    private const int MostOutputLines = 3000;

    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{Contract.Puerto}{Contract.Prefijo}"), Timeout = TimeSpan.FromMinutes(4) };
    private readonly ConcurrentQueue<string> _output = new();
    private readonly object _gate = new();
    private Process? _server;
    private readonly ConcurrentDictionary<int, string> _playerTokens = new();

    public Emulator()
    {
        Key = Environment.GetEnvironmentVariable("JONDO_TEST_KEY") is { Length: > 0 } key
            ? key
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string? source = Environment.GetEnvironmentVariable("JONDO_MCP_SOURCE");
        Source = string.IsNullOrWhiteSpace(source) ? Paths.Root : Path.GetFullPath(source);
        // Whatever was started from here goes with the tools: a server left behind would hold the
        // ports with a key nobody has any more.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public string Key { get; }

    public string Source { get; }

    public string DataRoot => Paths.Root;

    public bool StartedHere => _server is { HasExited: false };

    // ─── Calling it ─────────────────────────────────────────────────────────────────────────

    /// <summary>A control route, its body as JSON; the answer's body, or an McpException with what went wrong.</summary>
    public async Task<string> CallAsync(string route, JsonObject? body = null, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            var content = new StringContent((body ?? new JsonObject()).ToJsonString(), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            response = await _http.PostAsync(route, content, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            throw new McpException("The server is not running (nothing answers on 8888). Start it with server_start.");
        }
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode) return text;
        throw new McpException($"HTTP {(int)response.StatusCode} on /api/{route}: {text}");
    }

    /// <summary>The routes that only read: never waited on, whatever else the slot is doing.</summary>
    private static readonly HashSet<string> Reading = new(StringComparer.Ordinal)
    {
        "status", "token", "characters", "classes", "session", "fight", "world", "groups", "quests", "carried",
        "logs", "headless/frames", "items", "item", "spells", "wanted", "monsters",
    };

    /// <summary>One gate per test slot: the slot's actions one after the other.</summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _slotGates = new();

    /// <summary>
    /// A test route for a slot, with the key: { key, slot } added to the body. A slot's actions go
    /// one after the other, never two at once in the same session; what only reads is not held.
    /// Their ORDER is not kept: calls a client sends at once reach the tools in any order -- four
    /// replies to Oskar Khas sent together ran 25150 first, which his line does not offer, and the
    /// conversation closed. Steps that depend on one another go in one call (game_dialogue).
    /// </summary>
    public async Task<string> TestAsync(string route, JsonObject? body = null, int slot = 0, CancellationToken cancellationToken = default)
    {
        body ??= new JsonObject();
        body["key"] = Key;
        if (slot != 0) body["slot"] = slot;
        var gate = Reading.Contains(route) ? null : _slotGates.GetOrAdd(slot, _ => new SemaphoreSlim(1, 1));
        if (gate != null) await gate.WaitAsync(cancellationToken);
        try
        {
            return await CallAsync("test/" + route, body, cancellationToken);
        }
        catch (McpException ex) when (ex.Message.StartsWith("HTTP 404") && ex.Message.Contains("\"ruta\""))
        {
            throw new McpException(await TestRoutesMissingAsync(route, cancellationToken));
        }
        catch (McpException ex) when (ex.Message.StartsWith("HTTP 401") && ex.Message.Contains("\"key\""))
        {
            throw new McpException("The server was started with another JONDO_TEST_KEY. Restart it from here " +
                                   "(server_stop with force, then server_start), or give the tools its key.");
        }
        finally
        {
            gate?.Release();
        }
    }

    /// <summary>Why a test route answered 404: the route does not exist, or the server has no test routes at all.</summary>
    private async Task<string> TestRoutesMissingAsync(string route, CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync("test/status", new JsonObject { ["key"] = Key }, cancellationToken);
            return $"There is no test route '{route}'.";
        }
        catch (McpException)
        {
            return "The server answers but has no test routes: it was not started with JONDO_TEST_KEY. " +
                   "Stop it (server_stop with force) and start it from here with server_start.";
        }
    }

    /// <summary>Sends a control request with the test slot's launcher token.</summary>
    public async Task<string> PlayerAsync(string route, JsonObject? body = null, int slot = 0,
        CancellationToken cancellationToken = default)
    {
        string[] segments = route.Trim('/').Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new McpException("Provide the route below /api/, with no empty or relative path components.");
        string path = string.Join('/', segments.Select(Uri.EscapeDataString));
        var sent = body?.DeepClone().AsObject() ?? new JsonObject();
        for (int attempt = 0; ; attempt++)
        {
            if (!_playerTokens.TryGetValue(slot, out string? token))
            {
                token = Field(await TestAsync("token", slot: slot, cancellationToken: cancellationToken), "token");
                _playerTokens[slot] = token;
            }
            sent["token"] = token;
            try
            {
                return await CallAsync(path, sent, cancellationToken);
            }
            catch (McpException ex) when (attempt == 0 && ex.Message.StartsWith("HTTP 401", StringComparison.Ordinal))
            {
                _playerTokens.TryRemove(new KeyValuePair<int, string>(slot, token));
            }
        }
    }

    // ─── The process ────────────────────────────────────────────────────────────────────────

    /// <summary>What the server answers on /api/estado, or null when nothing does.</summary>
    public async Task<JsonElement?> StatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await CallAsync("estado", cancellationToken: cancellationToken));
            return doc.RootElement.Clone();
        }
        catch (McpException) { return null; }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    /// <summary>Whether the running server takes this run's key.</summary>
    public async Task<bool> HasTestRoutesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await CallAsync("test/status", new JsonObject { ["key"] = Key }, cancellationToken);
            return true;
        }
        catch (McpException) { return false; }
    }

    /// <summary>
    /// The server started headless with this run's key: built from the source with <c>dotnet run</c>,
    /// or the deployed executable at the data root. Waits until it is online.
    /// </summary>
    public async Task<string> StartAsync(bool fromSource, CancellationToken cancellationToken)
    {
        if (await StatusAsync(cancellationToken) is { } status)
        {
            if (await HasTestRoutesAsync(cancellationToken)) return "Already running, with the test routes. " + status;
            throw new McpException($"A server is already running without this run's test key (process " +
                                   $"{Field(status, "proceso")}). Stop it with server_stop force=true first.");
        }

        ProcessStartInfo start;
        if (fromSource)
        {
            string project = Path.Combine(Source, "Jondo.Unity.Server", "Jondo.Unity.Server.csproj");
            if (!File.Exists(project)) throw new McpException($"No server project at {project}.");
            start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(project)! };
            foreach (string argument in new[] { "run", "--project", project, "-c", "Debug", "--no-launch-profile", "--", "--headless" })
                start.ArgumentList.Add(argument);
        }
        else
        {
            string executable = Path.Combine(DataRoot, OperatingSystem.IsWindows() ? "Jondo Server.exe" : "Jondo Server");
            if (!File.Exists(executable)) throw new McpException($"No deployed server at {executable}.");
            start = new ProcessStartInfo(executable) { WorkingDirectory = DataRoot };
            start.ArgumentList.Add("--headless");
        }
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        start.Environment["JONDO_TEST_KEY"] = Key;
        start.Environment["JONDO_EMULATOR_ROOT"] = DataRoot;
        // Built from a checkout, its content goes with its code: the data root keeps the databases.
        string content = Path.Combine(Source, "content");
        if (fromSource && Directory.Exists(content)) start.Environment["JONDO_CONTENT_ROOT"] = content;

        _output.Clear();
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        if (!process.Start()) throw new McpException("The server process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_gate) _server = process;

        // The first build of the solution takes a while; the world's loading too.
        var until = DateTime.UtcNow.AddMinutes(6);
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new McpException($"The server stopped before coming online (exit {process.ExitCode}):\n{Tail(60)}");
            if (await StatusAsync(cancellationToken) is { } now && now.TryGetProperty("enLinea", out var online) && online.GetBoolean())
                return $"Online (process {process.Id}, {(fromSource ? "built from " + Source : "deployed executable")}, data at {DataRoot}" +
                       (start.Environment.TryGetValue("JONDO_CONTENT_ROOT", out var read) ? $", content from {read}" : "") + ").";
            await Task.Delay(1000, cancellationToken);
        }
        return $"Started (process {process.Id}) but not online after 6 minutes. Last lines:\n{Tail(40)}";
    }

    /// <summary>
    /// The server shut down as the launcher shuts it down, with the harness's administrator. With
    /// <paramref name="force"/>, a server that will not -- one without the test routes, or one that
    /// hangs -- is killed by its process number.
    /// </summary>
    public async Task<string> StopAsync(bool force, CancellationToken cancellationToken)
    {
        var status = await StatusAsync(cancellationToken);
        if (status == null)
        {
            Dispose();
            return "No server was running.";
        }
        int pid = int.TryParse(Field(status.Value, "proceso"), out int p) ? p : 0;
        bool asked = false;
        if (await HasTestRoutesAsync(cancellationToken))
        {
            await PlayerAsync("apagar", cancellationToken: cancellationToken);
            asked = true;
        }
        else if (!force)
        {
            throw new McpException("The server has no test routes, so it cannot be asked to stop. Use force=true to kill it.");
        }

        var until = DateTime.UtcNow.AddSeconds(asked ? 30 : 0);
        while (DateTime.UtcNow < until && await StatusAsync(cancellationToken) != null) await Task.Delay(500, cancellationToken);
        if (await StatusAsync(cancellationToken) == null)
        {
            Dispose();
            return "Stopped.";
        }
        if (!force) throw new McpException("The server was asked to stop and is still answering. Use force=true to kill it.");
        try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
        catch (ArgumentException) { }
        Dispose();
        return $"Killed process {pid}.";
    }

    /// <summary>The newest lines the server process wrote, when it was started from here.</summary>
    public string Tail(int lines) => string.Join('\n', _output.TakeLast(lines));

    private void Keep(string? line)
    {
        if (line == null) return;
        _output.Enqueue(line);
        while (_output.Count > MostOutputLines) _output.TryDequeue(out _);
    }

    public void Dispose()
    {
        Process? server;
        lock (_gate)
        {
            server = _server;
            _server = null;
        }
        _playerTokens.Clear();
        if (server == null) return;
        try { if (!server.HasExited) server.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        server.Dispose();
    }

    // ─── JSON ───────────────────────────────────────────────────────────────────────────────

    public static string Field(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        return Field(doc.RootElement, name);
    }

    public static string Field(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
            : "";

    /// <summary>A JSON object the caller wrote, or an empty one; an McpException when it is not one.</summary>
    public static JsonObject Object(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? throw new McpException("The body must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new McpException("The body is not valid JSON: " + ex.Message);
        }
    }
}
