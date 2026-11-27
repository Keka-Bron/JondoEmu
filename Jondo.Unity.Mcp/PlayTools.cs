using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Jondo.Unity.Mcp;

/// <summary>
/// A test slot's character played without the Dofus client: in the world over a socket of its own
/// (the server's HeadlessClient), and moved through the test routes. Typed shortcuts for the routes
/// used most; test_call reaches every other one.
/// </summary>
[McpServerToolType]
public static class PlayTools
{
    private const string SlotHelp = "The test slot: 0 is the jondoplay account, n is jondoplayNN.";

    [McpServerTool(Name = "characters", ReadOnly = true)]
    [Description("The slot's test account's characters: id, name, class (breed), level.")]
    public static Task<string> Characters(Emulator emulator, [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => emulator.TestAsync("characters", slot: slot, cancellationToken: cancellationToken);

    [McpServerTool(Name = "character_create")]
    [Description("A character for the slot's test account, beside the Astrub zaap (or at the tutorial's start). " +
                 "Breeds: 1 Feca, 2 Osamodas, 3 Anutrof, 4 Sram, 5 Xelor, 6 Zurcarák, 7 Aniripsa, 8 Yopuka, 9 Ocra, " +
                 "10 Sadida, 11 Sacrógrito, 12 Pandawa, 13 Tymador, 14 Zobal, 15 Steamer, 16 Selatrop, 17 Hipermago, 18 Uginak, 20 Forjalanza " +
                 "(the classes route lists those the server has).")]
    public static Task<string> CreateCharacter(Emulator emulator,
        [Description("The class (breed) number.")] int breed,
        [Description("Its level, 1 to 200.")] int level = 200,
        [Description("Its name (default Prueba-NN after the breed).")] string? name = null,
        [Description("0 male, 1 female.")] int sex = 0,
        [Description("Start at the guided tutorial, level 1, instead of Astrub.")] bool tutorial = false,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["breed"] = breed, ["level"] = level, ["sex"] = sex, ["tutorial"] = tutorial ? 1 : 0 };
        if (!string.IsNullOrWhiteSpace(name)) body["name"] = name;
        return emulator.TestAsync("create", body, slot, cancellationToken);
    }

    [McpServerTool(Name = "headless_connect")]
    [Description("Puts the slot's character in the world WITHOUT the Dofus client: a screenless client on its own " +
                 "socket that answers what the server needs (maps, turn acks, sequence acks, heartbeat, and 'ready' in " +
                 "placement when autoReady) and nothing else -- its turns are yours to play. Every test route then works " +
                 "on it. Create a character first if the account has none.")]
    public static Task<string> HeadlessConnect(Emulator emulator,
        [Description("The character's name; the account's first one when empty.")] string? character = null,
        [Description("Say 'ready' on its own when a fight's placement starts.")] bool autoReady = true,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
        => emulator.TestAsync("headless", new JsonObject { ["character"] = character ?? "", ["autoReady"] = autoReady }, slot, cancellationToken);

    [McpServerTool(Name = "headless_disconnect")]
    [Description("The slot's screenless client leaves, as a player who closes the game.")]
    public static Task<string> HeadlessDisconnect(Emulator emulator, [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => emulator.TestAsync("headless/stop", slot: slot, cancellationToken: cancellationToken);

    [McpServerTool(Name = "headless_send")]
    [Description("""
        Sends a client message from the slot's screenless client's own socket, so it goes through the server's whole
        socket loop as the real client's would, and returns the frames the server sent back while waiting (op and
        decoded fields). Fields by number: a number is a varint, a string a string, a list of numbers packed, an
        object a submessage; or the payload raw in `hex`. Some ops: hqa {1: group id} attacks a monster group (groups
        route), jrw {1: map, 2: [path corners]} then jqi walks, jqk {2: map} changes map, jwh {1: cell, 4: spell}
        casts, jxy ends the turn, kaq {1: 1} is 'ready', kme gives a fight up.
        """)]
    public static Task<string> HeadlessSend(Emulator emulator,
        [Description("The op, the message's short name (e.g. \"hqa\").")] string op,
        [Description("The fields as a JSON object, e.g. {\"1\": 123}.")] string? fields = null,
        [Description("Or the payload as hex.")] string? hex = null,
        [Description("How long to collect the server's answers, in ms (max 15000).")] int waitMs = 1000,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["op"] = op, ["waitMs"] = waitMs };
        if (!string.IsNullOrWhiteSpace(fields)) body["fields"] = Emulator.Object(fields);
        if (!string.IsNullOrWhiteSpace(hex)) body["hex"] = hex;
        return emulator.TestAsync("headless/send", body, slot, cancellationToken);
    }

    [McpServerTool(Name = "headless_frames", ReadOnly = true)]
    [Description("The frames the server sent the slot's screenless client after `since` (the lastFrame or a frame id " +
                 "of an earlier answer): what a real client would have received.")]
    public static Task<string> HeadlessFrames(Emulator emulator,
        [Description("Only frames after this id.")] long since = 0,
        [Description("At most this many, the newest.")] int most = 200,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
        => emulator.TestAsync("headless/frames", new JsonObject { ["since"] = since, ["most"] = most }, slot, cancellationToken);

    [McpServerTool(Name = "game_state", ReadOnly = true)]
    [Description("Where the slot's character is and what it is doing: its session (map, cell, level, kamas, fight) and, " +
                 "in a fight, the whole board (fighters with life, points, cells, buffs; turn order; marks).")]
    public static async Task<string> State(Emulator emulator, [Description(SlotHelp)] int slot = 0,
        [Description(CompanionHelp)] string? companion = null, CancellationToken cancellationToken = default)
    {
        string session = await emulator.TestAsync("session", AsCompanion(companion), slot, cancellationToken);
        string fight = await emulator.TestAsync("fight", AsCompanion(companion), slot, cancellationToken);
        return $"session: {session}\nfight: {fight}";
    }

    private const string CompanionHelp = "Act as this companion (his name or id), connected beside the slot's character, instead of " +
                                    "the slot's character. Any test_call body takes \"companion\" the same way.";

    /// <summary>A body that acts as a companion, or an empty one.</summary>
    internal static JsonObject AsCompanion(string? companion, JsonObject? body = null)
    {
        body ??= new JsonObject();
        if (!string.IsNullOrWhiteSpace(companion)) body["companion"] = companion.Trim();
        return body;
    }

    [McpServerTool(Name = "game_world", ReadOnly = true)]
    [Description("What is around the slot's character on its map: NPCs (with the quests they offer), interactives and " +
                 "their skills, and the monster groups.")]
    public static async Task<string> World(Emulator emulator, [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
    {
        string world = await emulator.TestAsync("world", slot: slot, cancellationToken: cancellationToken);
        string groups = await emulator.TestAsync("groups", slot: slot, cancellationToken: cancellationToken);
        return $"world: {world}\ngroups: {groups}";
    }

    [McpServerTool(Name = "game_command")]
    [Description("An administrator chat command typed by the slot's character (CommandHandler), e.g. \".item 39 1\", " +
                 "\".level 100\", \".oficio 26 80\". Returns whether it was handled and what the server logged.")]
    public static Task<string> Command(Emulator emulator,
        [Description("The command, with its dot.")] string text,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
        => emulator.TestAsync("command", new JsonObject { ["text"] = text }, slot, cancellationToken);

    [McpServerTool(Name = "game_dialogue")]
    [Description("Talks to an NPC and chooses the replies given, in that order, in one call: each line's text and the " +
                 "replies it offers are returned. Stops at the first reply the open line does not offer. Use it instead " +
                 "of several test_call reply sent together: calls sent at once reach the server in any order, and one reply " +
                 "out of turn closes the conversation.")]
    public static async Task<string> Dialogue(Emulator emulator,
        [Description("The NPC's id (game_world lists those on the map).")] int npc,
        [Description("The replies' ids, in the order they are chosen; empty to only open the conversation.")] int[]? replies = null,
        [Description(SlotHelp)] int slot = 0,
        [Description(CompanionHelp)] string? companion = null,
        CancellationToken cancellationToken = default)
    {
        var text = new System.Text.StringBuilder();
        var chosen = replies ?? Array.Empty<int>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                // Closed under it: right after a change of map, the map's late «loaded» (kmv) lets go
                // of what was open. Once more, from the start, a moment later.
                text.AppendLine("the conversation was closed by something else (a map just loaded?): once more.");
                await Task.Delay(1500, cancellationToken);
            }
            string answer = await emulator.TestAsync("talk", AsCompanion(companion, new JsonObject { ["npc"] = npc }), slot, cancellationToken);
            text.AppendLine("talk: " + Line(answer));
            bool closedUnder = false;
            for (int i = 0; i < chosen.Length; i++)
            {
                int reply = chosen[i];
                if (!Offers(answer, reply))
                {
                    text.AppendLine($"stopped: the open line does not offer reply {reply}.");
                    break;
                }
                answer = await emulator.TestAsync("reply", AsCompanion(companion, new JsonObject { ["reply"] = reply }), slot, cancellationToken);
                text.AppendLine($"{reply}: " + Line(answer));
                // A reply the line offered that leads nowhere, more to choose after it: not the end.
                if (i < chosen.Length - 1 && !IsOpen(answer) && answer.Contains("gives nothing", StringComparison.Ordinal))
                {
                    closedUnder = true;
                    break;
                }
            }
            if (!closedUnder) break;
        }
        return text.ToString().TrimEnd();
    }

    private static bool IsOpen(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("open", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    /// <summary>A conversation's answer told short: open or not, the line, its replies, what the server said.</summary>
    private static string Line(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        bool open = root.TryGetProperty("open", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.True;
        var said = new List<string>();
        if (open)
        {
            string line = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            said.Add($"line {Emulator.Field(root, "question")} «{(line.Length > 140 ? line[..140] + "…" : line)}»");
            if (root.TryGetProperty("replies", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Array)
                said.Add("replies " + string.Join(", ", r.EnumerateArray().Select(x => $"{Emulator.Field(x, "id")} «{Emulator.Field(x, "text")}»")));
        }
        else said.Add("closed");
        if (root.TryGetProperty("lines", out var lines) && lines.ValueKind == System.Text.Json.JsonValueKind.Array && lines.GetArrayLength() > 0)
            said.Add("server: " + string.Join(" | ", lines.EnumerateArray().Select(l => l.GetString())
                .Where(l => !string.IsNullOrEmpty(l) && !l.Contains("[client>server]", StringComparison.Ordinal) && !l.Contains("[server>client]", StringComparison.Ordinal))
                .TakeLast(4)));
        return string.Join("; ", said);
    }

    private static bool Offers(string json, int reply)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("replies", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Array
               && r.EnumerateArray().Any(x => x.TryGetProperty("id", out var id) && id.GetInt64() == reply);
    }

    [McpServerTool(Name = "game_goto")]
    [Description("Teleports the slot's character to a map (and cell), out of a fight.")]
    public static Task<string> Goto(Emulator emulator,
        [Description("The map id.")] long map,
        [Description("The cell; the map's own landing cell when negative.")] int cell = -1,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["map"] = map };
        if (cell >= 0) body["cell"] = cell;
        return emulator.TestAsync("goto", body, slot, cancellationToken);
    }

    [McpServerTool(Name = "game_give")]
    [Description("Gives the slot's character an item by template id (a negative quantity takes it back).")]
    public static Task<string> Give(Emulator emulator,
        [Description("The item template id (gid).")] int item,
        [Description("How many.")] int quantity = 1,
        [Description(SlotHelp)] int slot = 0,
        [Description(CompanionHelp)] string? companion = null,
        CancellationToken cancellationToken = default)
        => emulator.TestAsync("give", AsCompanion(companion, new JsonObject { ["item"] = item, ["quantity"] = quantity }), slot, cancellationToken);

    [McpServerTool(Name = "fight_team")]
    [Description("Starts a fight for the slot's character against JondoBots of the classes given (and with JondoBot " +
                 "allies), on its map or the one given. The bots are driven by this slot: their turns are yours too.")]
    public static Task<string> Team(Emulator emulator,
        [Description("The foes' breeds, e.g. [8, 1].")] int[] foes,
        [Description("Allied bots' breeds.")] int[]? allies = null,
        [Description("Control allied fighters from the client.")] bool companions = false,
        [Description("The arena map (0: the character's own).")] long map = 0,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["foes"] = new JsonArray(foes.Select(f => (JsonNode?)f).ToArray()),
            ["allies"] = new JsonArray((allies ?? Array.Empty<int>()).Select(a => (JsonNode?)a).ToArray()),
            ["companions"] = companions,
        };
        if (map > 0) body["map"] = map;
        return emulator.TestAsync("team", body, slot, cancellationToken);
    }

    [McpServerTool(Name = "fight_cast")]
    [Description("Casts a spell as the client casts it, waiting for the fighter's turn, and returns what changed on " +
                 "the board, each effect's verdict against what the spell should do, the fight's invariants, and the " +
                 "server's lines. Use fight_stage first to stand where the spell reaches its target.")]
    public static Task<string> Cast(Emulator emulator,
        [Description("The spell id.")] int spell,
        [Description("The target cell.")] int cell,
        [Description("Cast as this fighter id (a bot or summon this slot plays); 0 for the slot's character.")] long @as = 0,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["spell"] = spell, ["cell"] = cell };
        if (@as != 0) body["as"] = @as;
        return emulator.TestAsync("cast", body, slot, cancellationToken);
    }

    [McpServerTool(Name = "fight_stage")]
    [Description("Puts the fighter on a free cell from which the spell reaches and sees its target.")]
    public static Task<string> Stage(Emulator emulator,
        [Description("The spell id.")] int spell,
        [Description("The target fighter id (or 0 and give a cell).")] long target = 0,
        [Description("The target cell, when no fighter is named.")] int cell = -1,
        [Description("As this fighter id; 0 for the slot's character.")] long @as = 0,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["spell"] = spell };
        if (target != 0) body["target"] = target;
        else body["cell"] = cell;
        if (@as != 0) body["as"] = @as;
        return emulator.TestAsync("stage", body, slot, cancellationToken);
    }

    [McpServerTool(Name = "fight_end_turn")]
    [Description("Ends the slot's character's turn and waits until it comes back: every spell the others cast meanwhile " +
                 "is returned, judged effect by effect, with the fight's invariants.")]
    public static Task<string> EndTurn(Emulator emulator,
        [Description("Stop at the turn of an ally the character plays instead of passing it.")] bool allies = false,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
        => emulator.TestAsync("endturn", new JsonObject { ["allies"] = allies ? 1 : 0 }, slot, cancellationToken);
}
