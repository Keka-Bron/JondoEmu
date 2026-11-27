using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Jondo.Unity.Mcp;

/// <summary>The server itself: started, stopped, read, and any of its control routes called.</summary>
[McpServerToolType]
public static class ServerTools
{
    [McpServerTool(Name = "server_status", ReadOnly = true)]
    [Description("Whether the emulator server answers on 8888, whether it has the test routes for this run's key, " +
                 "and which folders the tools use (data root, source checkout).")]
    public static async Task<string> Status(Emulator emulator, GameClients clients, CancellationToken cancellationToken)
    {
        var status = await emulator.StatusAsync(cancellationToken);
        var text = new StringBuilder();
        text.AppendLine(status == null ? "Server: not running." : $"Server: {status}");
        if (status != null) text.AppendLine($"Test routes: {(await emulator.HasTestRoutesAsync(cancellationToken) ? "yes" : "NO (not started with this run's JONDO_TEST_KEY)")}");
        text.AppendLine($"Started from here: {emulator.StartedHere}");
        text.AppendLine($"Data root: {emulator.DataRoot}");
        text.AppendLine($"Source: {emulator.Source}");
        text.Append(clients.Describe(0));
        return text.ToString();
    }

    [McpServerTool(Name = "server_start")]
    [Description("Starts the server headless with the test routes on, and waits until it is online (up to 6 minutes: " +
                 "the first build is slow). fromSource=true builds and runs Jondo.Unity.Server from the source checkout " +
                 "with `dotnet run` (what you want after changing server code); false runs the deployed 'Jondo Server.exe'. " +
                 "Fails if another server without this run's key is already running.")]
    public static Task<string> Start(Emulator emulator,
        [Description("Build and run from the source checkout (true) or run the deployed executable (false).")] bool fromSource = true,
        CancellationToken cancellationToken = default)
        => emulator.StartAsync(fromSource, cancellationToken);

    [McpServerTool(Name = "server_stop", Destructive = true)]
    [Description("Shuts the server down as the launcher does (players are saved). force=true kills it by its process " +
                 "number when it cannot be asked: a server started by hand without the test routes, or one that hangs.")]
    public static Task<string> Stop(Emulator emulator,
        [Description("Kill the process if it cannot be shut down cleanly.")] bool force = false,
        CancellationToken cancellationToken = default)
        => emulator.StopAsync(force, cancellationToken);

    [McpServerTool(Name = "server_restart", Destructive = true)]
    [Description("server_stop then server_start: to try a server change. Clients and headless slots are disconnected.")]
    public static async Task<string> Restart(Emulator emulator,
        [Description("Build and run from the source checkout (true) or run the deployed executable (false).")] bool fromSource = true,
        CancellationToken cancellationToken = default)
    {
        string stopped = await emulator.StopAsync(force: true, cancellationToken);
        return stopped + "\n" + await emulator.StartAsync(fromSource, cancellationToken);
    }

    [McpServerTool(Name = "server_logs", ReadOnly = true)]
    [Description("The server's console lines (the launcher window's log), newest last. Pass the lastId of the previous " +
                 "call as `since` to get only what is new: after an action, read what the server said about it.")]
    public static async Task<string> Logs(Emulator emulator,
        [Description("Only lines after this id (0: everything kept).")] long since = 0,
        [Description("Only lines containing this text, case aside.")] string? contains = null,
        [Description("At most this many lines, the newest.")] int max = 200,
        [Description("Keep the lines of the tools' own calls to /api/test (left out by default).")] bool includeHarness = false,
        CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(await emulator.TestAsync("logs", new() { ["since"] = since }, cancellationToken: cancellationToken));
        var lines = doc.RootElement.GetProperty("logs").EnumerateArray()
            .Select(l => (Id: l.GetProperty("id").GetInt64(), Time: l.GetProperty("time").GetString(), Msg: l.GetProperty("msg").GetString() ?? ""))
            .Where(l => includeHarness || !IsHarness(l.Msg))
            .Where(l => string.IsNullOrEmpty(contains) || l.Msg.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .TakeLast(Math.Clamp(max, 1, 5000))
            .Select(l => $"{l.Id} {l.Time} {l.Msg}");
        return string.Join('\n', lines) + $"\nlastId={doc.RootElement.GetProperty("lastId").GetInt64()}";
    }

    /// <summary>
    /// A line the HAAPI writes for one of the tools' own calls: the request, and its body, which
    /// carries the key (censored) as only the test routes' bodies do.
    /// </summary>
    private static bool IsHarness(string line)
        => line.StartsWith("[HAAPI] POST /api/test/", StringComparison.Ordinal)
           || line.StartsWith("[HAAPI]  body:", StringComparison.Ordinal) && line.Contains("\"key\":", StringComparison.Ordinal);

    [McpServerTool(Name = "server_output", ReadOnly = true)]
    [Description("The raw stdout/stderr of the server process started with server_start: build errors, and what it " +
                 "wrote before its log buffer existed or after it died.")]
    public static string Output(Emulator emulator, [Description("How many of the newest lines.")] int lines = 80)
        => emulator.Tail(Math.Clamp(lines, 1, 3000)) is { Length: > 0 } tail ? tail : "(nothing: the server was not started from here, or wrote nothing)";

    [McpServerTool(Name = "run_tests")]
    [Description("Runs Jondo.Unity.Tests (dotnet test) on a checkout -- the source by default, or a worktree's path -- " +
                 "with JONDO_EMULATOR_ROOT on that checkout, so the tests read its content. Its bases/ get the data root's " +
                 "databases when it has none (a worktree: bases/ is not in git), copied, so the tests never write into " +
                 "the data root's. Built where the tests expect (no --artifacts-path). Answers the summary and each failure " +
                 "with its message. A data root that holds a real server's data can fail the few tests that count a whole " +
                 "table (MarketplaceTests' sales history): delete the checkout's bases/ to start from a fresh copy, or " +
                 "copy a clean world.db there.")]
    public static Task<string> RunTests(Emulator emulator,
        [Description("A dotnet test --filter, e.g. \"FullyQualifiedName~DungeonTests\"; empty for every test.")] string? filter = null,
        [Description("The checkout's folder; the source checkout when empty.")] string? path = null,
        [Description("The most failures told in full.")] int maxFailures = 20,
        CancellationToken cancellationToken = default)
        => TestRunner.RunAsync(emulator, string.IsNullOrWhiteSpace(path) ? emulator.Source : Path.GetFullPath(path),
                               filter, Math.Clamp(maxFailures, 1, 200), cancellationToken);

    [McpServerTool(Name = "protocol_fields", ReadOnly = true)]
    [Description("What the fields of a game message mean, where it is known: the client's message names are rotated " +
                 "(ind { fqfe, fqff, fqfg }), so the meaning of each field number is kept in docs/protocol_fields.json, " +
                 "with how it was settled -- a capture, the client's code, a fix checked in the client. Without op, every " +
                 "message listed. Add to the file whenever a field's meaning is settled.")]
    public static string ProtocolFields(Emulator emulator,
        [Description("The message's short name (e.g. \"ind\"); empty for all.")] string? op = null)
        => ProtocolNotes.Describe(emulator.Source, op);

    [McpServerTool(Name = "test_call")]
    [Description("""
        Calls any /api/test/<route> of the server (the test harness's routes, ControlApiTest.cs and its partials),
        with the key and the slot added. The body is a JSON object. They act on the slot's test account
        (jondoplay, jondoplay01...), which must be in the world (headless_connect or a client) for most of them.
        Add "companion": name or id to any body to act as a companion connected beside the slot's character without taking him in hand. A slot's actions run one after the other (reads are not held), but
        calls sent at once arrive in any order: never send together calls whose order matters (an NPC's replies:
        use game_dialogue).
        Routes and bodies:
          status, characters, classes, token, world, session, fight, groups, quests, reload, close
          create {breed, level, name?, sex?, tutorial?}      spells {breed, level}     spell {spell, level?, grade?}
          command {text}  (an admin chat command, e.g. ".item 39 1")   goto {map, cell?}   give {item, quantity, souls?}
          talk {npc|actor, action?}   reply {reply}   use {element, skill?}   useitem {item}   carried {item}
          quest {quest, op: start|finish|forget}   wanted {monster?}   items {group}   item {gid}   equip {gid|none}
          team {allies:[breed], foes:[breed], blue:[slot], red:[slot], companions?, map?}   (starts a fight vs JondoBots)
          cast {spell, cell, as?}   walk {fighter, cell, mark?}   place {fighter?, cell}   stage {spell, target|cell, as?, free?}
          sight {target, cells:[]}   refresh {keep?, noSummons?, hurt?, hurtTo?, as?}   endturn {allies?}
          state {as?, state, rounds}   crit {as?, on}   kill {fighter, by?}   finish {team?}
          inject {op, fields:{"1":n}}   (a client message handled in the session, without a socket)
          scenario / scenarios  (see ControlApiScenarios.cs)
          headless {character?, autoReady?}   headless/stop   headless/send {op, fields?, hex?, waitMs?}   headless/frames {since?, most?}   logs {since?}
        """)]
    public static Task<string> TestCall(Emulator emulator,
        [Description("The route after /api/test/, e.g. \"session\" or \"headless/send\".")] string route,
        [Description("The body, a JSON object (may be empty).")] string? body = null,
        [Description("The test slot: 0 is jondoplay, n is jondoplayNN (several characters at once).")] int slot = 0,
        CancellationToken cancellationToken = default)
        => emulator.TestAsync(route.Trim('/'), Emulator.Object(body), slot, cancellationToken);

    [McpServerTool(Name = "player_call")]
    [Description("Calls a control route under /api/ with the test slot's account token. Use the installed mod's documentation for its routes and request fields.")]
    public static Task<string> PlayerCall(Emulator emulator,
        [Description("The route after /api/.")] string route,
        [Description("The body, a JSON object (may be empty).")] string? body = null,
        [Description("The test slot: 0 is jondoplay, n is jondoplayNN.")] int slot = 0,
        CancellationToken cancellationToken = default)
        => emulator.PlayerAsync(route, Emulator.Object(body), slot, cancellationToken);

    [McpServerTool(Name = "admin_call")]
    [Description("""
        Calls an administrator route of the control API (/api/<route>) as the test harness's administrator account,
        its token added. The ones the in-game admin window (F10) uses: conectados, mapa, invocar, quitar, carcel,
        liberar, presos, regalo, coordenadas, buscar-mapas, ficha, niveles-monstruos, personaje (POST
        {personaje?, nivel?, kamas?, vitalidad?..., objeto?, cantidad?, mapa?, celda?}), rol {cuenta, rol}, registro {desde}.
        See Jondo.Unity.Server/Network/ControlApi*.cs for each body.
        """)]
    public static Task<string> AdminCall(Emulator emulator,
        [Description("The route after /api/, e.g. \"conectados\".")] string route,
        [Description("The body, a JSON object (may be empty).")] string? body = null,
        CancellationToken cancellationToken = default)
        => emulator.PlayerAsync(route, Emulator.Object(body), cancellationToken: cancellationToken);
}
