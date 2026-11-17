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
    public static async Task<string> State(Emulator emulator, [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
    {
        string session = await emulator.TestAsync("session", slot: slot, cancellationToken: cancellationToken);
        string fight = await emulator.TestAsync("fight", slot: slot, cancellationToken: cancellationToken);
        return $"session: {session}\nfight: {fight}";
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
        CancellationToken cancellationToken = default)
        => emulator.TestAsync("give", new JsonObject { ["item"] = item, ["quantity"] = quantity }, slot, cancellationToken);

    [McpServerTool(Name = "fight_team")]
    [Description("Starts a fight for the slot's character against JondoBots of the classes given (and with JondoBot " +
                 "allies), on its map or the one given. The bots are driven by this slot: their turns are yours too.")]
    public static Task<string> Team(Emulator emulator,
        [Description("The foes' breeds, e.g. [8, 1].")] int[] foes,
        [Description("Allied bots' breeds.")] int[]? allies = null,
        [Description("Play the allies as heroes, from the client (hero mode).")] bool heroes = false,
        [Description("The arena map (0: the character's own).")] long map = 0,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["foes"] = new JsonArray(foes.Select(f => (JsonNode?)f).ToArray()),
            ["allies"] = new JsonArray((allies ?? Array.Empty<int>()).Select(a => (JsonNode?)a).ToArray()),
            ["heroes"] = heroes,
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
