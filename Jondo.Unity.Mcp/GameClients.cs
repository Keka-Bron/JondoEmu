using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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
/// test account: the launch comes from /api/test/launch, the mods from the source checkout. The
/// pointer and the keyboard are the mod's own, so a client in the background plays without taking
/// the computer's mouse, and several play at once.
/// </remarks>
public sealed class GameClients : IDisposable
{
    /// <summary>The mods the emulator ships, each in its own folder of the checkout.</summary>
    private static readonly string[] ShippedMods = { "JondoFix", "Cazacabezas", "Heroes" };

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
        string installed = InstallMods(installation.Root);

        var launch = Emulator.Object(await _emulator.TestAsync("launch", new JsonObject { ["language"] = language }, slot, cancellationToken));
        string token = (string?)launch["token"] ?? "";
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
        string mods = Path.Combine(clientRoot, "Mods");
        var said = new List<string>();
        foreach (string mod in ShippedMods)
        {
            string shipped = Path.Combine(_emulator.Source, mod, mod + ".dll");
            if (!File.Exists(shipped)) continue;
            string installed = Path.Combine(mods, mod + ".dll");
            string aside = installed + ".old";
            try
            {
                try { if (File.Exists(aside)) File.Delete(aside); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (File.Exists(installed) && SameContent(installed, shipped)) continue;
                Directory.CreateDirectory(mods);
                try
                {
                    File.Copy(shipped, installed, overwrite: true);
                }
                catch (IOException) when (File.Exists(installed) && !File.Exists(aside))
                {
                    // A client open holds it: a loaded file can be renamed, not overwritten.
                    File.Move(installed, aside);
                    File.Copy(shipped, installed, overwrite: true);
                }
                said.Add(mod);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                said.Add($"{mod} NOT updated ({ex.Message})");
            }
        }
        return said.Count == 0 ? "Mods already up to date." : "Mods updated: " + string.Join(", ", said) + ".";
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var streamA = File.OpenRead(a);
        using var streamB = File.OpenRead(b);
        return SHA256.HashData(streamA).AsSpan().SequenceEqual(SHA256.HashData(streamB));
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
