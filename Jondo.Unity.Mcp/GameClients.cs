using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jondo.Unity.Launcher;
using ModelContextProtocol;

namespace Jondo.Unity.Mcp;

/// <summary>
/// The real Dofus clients started from here, one per test slot, and the line protocol JondoFix
/// listens with on each one's JONDO_INPUT_PORT (JondoFix/TestInput.cs): pointer and keyboard of its
/// own, the interface read from UI Toolkit, and a picture of the window.
/// </summary>
/// <remarks>
/// A client is started as the launcher starts one (LauncherService.ClientStartInfo), for the slot's
/// test account: the launch comes from /api/test/launch, the mods from the running server's manifest. The
/// pointer and the keyboard are the mod's own, so a client in the background plays without taking
/// the computer's mouse, and several play at once.
/// </remarks>
public sealed class GameClients : IDisposable
{
    private readonly Emulator _emulator;
    private readonly ConcurrentDictionary<int, Running> _running = new();

    private sealed record Running(Process Process, int Port, int Instance);

    public GameClients(Emulator emulator)
    {
        _emulator = emulator;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    /// <summary>
    /// The slot's client started, logged in by the launch as the launcher logs one in, and waited
    /// for until its input port answers (MelonLoader's first start can take minutes).
    /// </summary>
    public async Task<string> LaunchAsync(int slot, int width, int height, string language, CancellationToken cancellationToken)
    {
        if (_running.TryGetValue(slot, out var before) && !before.Process.HasExited)
            throw new McpException($"Slot {slot} already has a client (process {before.Process.Id}). Close it first.");
        if (!OperatingSystem.IsWindows())
            throw new McpException("The input port is JondoFix's, a MelonLoader mod: only the Windows client has it.");

        var installation = ClientInstallation.Find()
            ?? throw new McpException("No Dofus client found. Set JONDO_CLIENT_EXECUTABLE to its Dofus.exe.");
        if (!Directory.Exists(Path.Combine(installation.Root, "MelonLoader")))
            throw new McpException($"The client at {installation.Root} has no MelonLoader: the mods, and the input port, would not load.");
        var launch = Emulator.Object(await _emulator.TestAsync("launch", new JsonObject { ["language"] = language }, slot, cancellationToken));
        string token = (string?)launch["token"] ?? "";
        // The test launch has authenticated the local developer server before accepting its manifest.
        var server = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), Contract.Puerto).Uri;
        string cache = Path.Combine(UserDirectories.Data, "Jondo", "client-mods");
        var modPlan = ClientModDeployment.Prepare(server, _emulator.Source, cache, "",
            hasMelonLoader: true, requiresRedirectMod: true, approve: (_, _, _) => true);
        string installed = modPlan.UsesManifest
            ? "Using the server's client mod manifest."
            : InstallMods(installation.Root);
        string hash = (string?)launch["hash"] ?? "";
        int instance = (int?)launch["instance"] ?? 0;
        int role = (int?)launch["role"] ?? 0;
        int port = FreePort();

        var start = new ProcessStartInfo(installation.Executable) { WorkingDirectory = installation.Root, UseShellExecute = false };
        start.Environment["ZAAP_PORT"] = "15881";
        start.Environment["ZAAP_HASH"] = hash;
        start.Environment["ZAAP_GAME"] = "dofus";
        start.Environment["ZAAP_RELEASE"] = "dofus3";
        start.Environment["ZAAP_INSTANCE_ID"] = instance.ToString(CultureInfo.InvariantCulture);
        start.Environment["ZAAP_CAN_AUTH"] = "true";
        start.Environment["JONDO_EMULATOR_ROOT"] = _emulator.DataRoot;
        start.Environment["JONDO_INPUT_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        start.Environment["JONDO_ACCOUNT_ROLE"] = role.ToString(CultureInfo.InvariantCulture);
        start.Environment["JONDO_CONTROL_TOKEN"] = token;
        start.Environment["JONDO_PLAYER_TOKEN"] = token;
        ClientModDeployment.Activate(installation.Root, _emulator.Source, modPlan, start);
        foreach (string argument in new[]
                 {
                     "-force-d3d11", "-screen-fullscreen", "0",
                     "-screen-width", width.ToString(CultureInfo.InvariantCulture),
                     "-screen-height", height.ToString(CultureInfo.InvariantCulture),
                     "--melonloader.hideconsole", "--melonloader.disablestartscreen",
                     "--port", "15881", "--gameName", "dofus", "--gameRelease", "dofus3",
                     "--instanceId", instance.ToString(CultureInfo.InvariantCulture), "--hash", hash,
                     "--canLogin", "true", "--langCode", language, "--autoConnectType", "1", "--connectionPort", "5555",
                 })
            start.ArgumentList.Add(argument);

        var process = Process.Start(start) ?? throw new McpException("The client did not start.");
        _running[slot] = new Running(process, port, instance);

        var until = DateTime.UtcNow.AddMinutes(4);
        string ping = "";
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                _running.TryRemove(slot, out _);
                throw new McpException($"The client exited (code {process.ExitCode}) before its input port answered. " +
                                       $"Look at {Path.Combine(installation.Root, "MelonLoader", "Latest.log")}.");
            }
            try
            {
                ping = await SendAsync(slot, "ping", TimeSpan.FromSeconds(3), cancellationToken);
                break;
            }
            catch (McpException) { await Task.Delay(1500, cancellationToken); }
        }
        return $"Client for slot {slot}: process {process.Id}, input port {port}, instance {instance}. {installed}" +
               (ping.Length > 0 ? $"\nping: {ping}\nIt logs in on its own; pick the character on screen (client_screenshot, client_ui)."
                                : "\nThe input port has not answered yet: MelonLoader may still be starting. Try client_status in a while.");
    }

    /// <summary>The emulator's mods into the client's Mods, each when it differs, as the launcher does.</summary>
    private string InstallMods(string clientRoot)
    {
        var updates = BundledClientMods.Install(_emulator.Source, clientRoot);
        return updates.Count == 0 ? "Mods already up to date." : "Mods updated: " +
            string.Join(", ", updates.Select(update => update.Error == null
                ? update.Name : $"{update.Name} failed ({update.Error})")) + ".";
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// One command to the slot's client, as a line; its answer, without the «ok». A command is
    /// answered once the client has played it, frame by frame.
    /// </summary>
    public async Task<string> SendAsync(int slot, string line, TimeSpan? patience = null, CancellationToken cancellationToken = default)
    {
        if (!_running.TryGetValue(slot, out var client))
            throw new McpException($"No client started from here for slot {slot}. Use client_launch.");
        if (client.Process.HasExited)
        {
            _running.TryRemove(slot, out _);
            throw new McpException($"The client of slot {slot} has exited (code {client.Process.ExitCode}).");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(patience ?? TimeSpan.FromSeconds(30));
        string reply;
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, client.Port, timeout.Token);
            using var stream = tcp.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(line.AsMemory(), timeout.Token);
            reply = await reader.ReadLineAsync(timeout.Token) ?? "error: the client closed the connection";
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new McpException($"The client of slot {slot} did not answer '{line.Split(' ')[0]}': {ex.Message}");
        }
        if (reply.StartsWith("ok", StringComparison.Ordinal)) return reply.Length > 3 ? reply[3..] : "";
        throw new McpException(reply.StartsWith("error: ", StringComparison.Ordinal) ? reply[7..] : reply);
    }

    /// <summary>
    /// The client's MelonLoader log, numbered from 1, after <paramref name="since"/>: read shared,
    /// the client writing it all along. A log shorter than <paramref name="since"/> was started anew
    /// by a new client, and is read from its start.
    /// </summary>
    public string ReadLog(int since, string? contains, int max)
    {
        var installation = ClientInstallation.Find() ?? throw new McpException("No Dofus client found.");
        string path = Path.Combine(installation.Root, "MelonLoader", "Latest.log");
        if (!File.Exists(path)) return "(no log yet)";
        var lines = new List<string>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            string? line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);
        }
        string note = "";
        if (since > lines.Count)
        {
            note = "(the log was started anew by a new client)\n";
            since = 0;
        }
        var picked = lines.Select((text, i) => (Number: i + 1, Text: text))
            .Where(l => l.Number > since && (string.IsNullOrEmpty(contains) || l.Text.Contains(contains, StringComparison.OrdinalIgnoreCase)))
            .TakeLast(max)
            .Select(l => $"{l.Number}: {l.Text}");
        return note + string.Join('\n', picked) + $"\nlastLine={lines.Count}";
    }

    /// <summary>One text the client's interface shows (client_ui's answer).</summary>
    private sealed record Shown(string T, int X, int Y, int W, int H, string N);

    private async Task<List<Shown>> ShownAsync(int slot, CancellationToken cancellationToken)
    {
        string json = await SendAsync(slot, "ui", TimeSpan.FromSeconds(15), cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => new Shown(
            e.GetProperty("t").GetString() ?? "", e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32(),
            e.GetProperty("w").GetInt32(), e.GetProperty("h").GetInt32(), e.TryGetProperty("n", out var n) ? n.GetString() ?? "" : "")).ToList();
    }

    private Task ClickAsync(int slot, Shown on, CancellationToken cancellationToken)
        => SendAsync(slot, FormattableString.Invariant($"click {on.X + on.W / 2} {on.Y + on.H / 2} 1"), cancellationToken: cancellationToken);

    /// <summary>
    /// The slot's character in the world through the real client, from wherever it is: started when
    /// there is none, the server's JOUER, the character picked on the list and his JOUER, until the
    /// server has him in the world. A client stuck on its connection screen -- one left over a
    /// server's restart, its JOUER greyed for ever -- is closed and started again, once.
    /// </summary>
    public async Task<string> EnterWorldAsync(int slot, string character, int width, int height, string language,
                                              CancellationToken cancellationToken)
    {
        var said = new List<string>();
        if (!_running.TryGetValue(slot, out var running) || running.Process.HasExited)
            said.Add(await LaunchAsync(slot, width, height, language, cancellationToken));

        bool relaunched = false;
        string lastScreen = "";
        var screenSince = DateTime.UtcNow;
        var until = DateTime.UtcNow.AddMinutes(4);
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? inWorld = await InWorldAsync(slot, cancellationToken);
            if (inWorld != null)
            {
                if (string.Equals(inWorld, character, StringComparison.OrdinalIgnoreCase))
                    return string.Join('\n', said.Append($"{character} is in the world.").Where(s => s.Length > 0));
                throw new McpException($"The slot's client is in the world as {inWorld}, not {character}. Use client_close and client_enter_world again.");
            }

            List<Shown> shown;
            try { shown = await ShownAsync(slot, cancellationToken); }
            catch (McpException) { await Task.Delay(1500, cancellationToken); continue; }

            string screen = string.Join('|', shown.Select(s => s.T).OrderBy(t => t, StringComparer.Ordinal));
            if (screen != lastScreen) { lastScreen = screen; screenSince = DateTime.UtcNow; }
            var play = shown.FirstOrDefault(s => s.N == "playButton_label" && s.T.Equals("JOUER", StringComparison.OrdinalIgnoreCase))
                       ?? shown.FirstOrDefault(s => s.N == "playButton_label");
            // The character list has the one picked drawn large (lbl_nameMain); the servers' screen
            // has lbl_names too, for the servers and the account, and none of those.
            bool characterList = shown.Any(s => s.N == "lbl_nameMain");
            var names = shown.Where(s => s.N == "lbl_name").ToList();
            var wanted = names.FirstOrDefault(s => string.Equals(s.T, character, StringComparison.OrdinalIgnoreCase));

            if (characterList && wanted != null)
            {
                await ClickAsync(slot, wanted, cancellationToken);
                await Task.Delay(500, cancellationToken);
                if (play != null) await ClickAsync(slot, play, cancellationToken);
            }
            else if (characterList && DateTime.UtcNow - screenSince > TimeSpan.FromSeconds(10))
            {
                throw new McpException($"{character} is not on the slot's character list ({string.Join(", ", names.Select(n => n.T))}).");
            }
            else if (!characterList && play != null)
            {
                await ClickAsync(slot, play, cancellationToken);
            }

            // The same screen for long, nothing in the world: the connection screen's JOUER greyed
            // after a server restart. A new client logs in afresh.
            if (DateTime.UtcNow - screenSince > TimeSpan.FromSeconds(40))
            {
                if (relaunched) throw new McpException("The client stays on the same screen even after a new start: " +
                                                       "client_screenshot to see it.");
                relaunched = true;
                Close(slot);
                said.Add("The client was stuck on its connection screen: started again.");
                said.Add(await LaunchAsync(slot, width, height, language, cancellationToken));
                lastScreen = "";
                screenSince = DateTime.UtcNow;
            }
            await Task.Delay(1500, cancellationToken);
        }
        throw new McpException($"{character} was not in the world after 4 minutes: client_screenshot to see where it stopped.");
    }

    /// <summary>The name of the slot's character in the world, or null.</summary>
    private async Task<string?> InWorldAsync(int slot, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _emulator.TestAsync("session", slot: slot, cancellationToken: cancellationToken));
            return doc.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch (McpException) { return null; }
    }

    public string Describe(int slot)
        => !_running.TryGetValue(slot, out var client) ? $"No client started from here for slot {slot}."
         : client.Process.HasExited ? $"The client of slot {slot} has exited (code {client.Process.ExitCode})."
         : $"Client of slot {slot}: process {client.Process.Id}, input port {client.Port}, instance {client.Instance}.";

    public string Close(int slot)
    {
        if (!_running.TryRemove(slot, out var client)) return $"No client started from here for slot {slot}.";
        try { if (!client.Process.HasExited) client.Process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        client.Process.Dispose();
        return $"Client of slot {slot} closed.";
    }

    public void Dispose()
    {
        foreach (int slot in _running.Keys.ToList()) Close(slot);
    }
}
