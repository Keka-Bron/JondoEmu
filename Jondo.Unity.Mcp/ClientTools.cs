using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Jondo.Unity.Mcp;

/// <summary>
/// The real Dofus client of a test slot: started, looked at, clicked and typed into through JondoFix's
/// input port. Coordinates are the window's pixels, (0, 0) top left -- the ones client_ui answers with.
/// </summary>
[McpServerToolType]
public static class ClientTools
{
    private const string SlotHelp = "The test slot whose client it is: 0 is the jondoplay account, n is jondoplayNN.";

    [McpServerTool(Name = "client_launch")]
    [Description("Starts the real Dofus client for the slot's test account, as the launcher does, with the mods of the " +
                 "source checkout and JondoFix's input port on. It logs in on its own; then pick the character on " +
                 "screen. Waits up to 4 minutes for the input port. The server must be running with the test routes, and " +
                 "the slot must not be connected headless.")]
    public static Task<string> Launch(GameClients clients,
        [Description("Window width in pixels.")] int width = 1600,
        [Description("Window height in pixels.")] int height = 900,
        [Description("The game's language (--langCode): fr, es, en...")] string language = "fr",
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
        => clients.LaunchAsync(slot, width, height, language, cancellationToken);

    [McpServerTool(Name = "client_enter_world")]
    [Description("The slot's character in the world through the real client, in one call: the client started if there " +
                 "is none, the server's JOUER, the character picked on the list and his JOUER, until the server has him " +
                 "in the world. A client stuck on its connection screen (after a server restart) is started again. " +
                 "Fails if the client is in the world as another character.")]
    public static Task<string> EnterWorld(GameClients clients,
        [Description("The character's name.")] string character,
        [Description(SlotHelp)] int slot = 0,
        [Description("Window width, when the client has to be started.")] int width = 1600,
        [Description("Window height, when the client has to be started.")] int height = 900,
        [Description("The game's language, when the client has to be started.")] string language = "fr",
        CancellationToken cancellationToken = default)
        => clients.EnterWorldAsync(slot, character, width, height, language, cancellationToken);

    [McpServerTool(Name = "client_status", ReadOnly = true)]
    [Description("Whether the slot's client is alive and its game loop running: window size, focus, the frame it is on " +
                 "(two calls with the same frame: frozen).")]
    public static async Task<string> Status(GameClients clients, [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
    {
        string described = clients.Describe(slot);
        try { return described + "\nping: " + await clients.SendAsync(slot, "ping", TimeSpan.FromSeconds(5), cancellationToken); }
        catch (ModelContextProtocol.McpException ex) { return described + "\nping failed: " + ex.Message; }
    }

    [McpServerTool(Name = "client_close", Destructive = true)]
    [Description("Kills the slot's client.")]
    public static string Close(GameClients clients, [Description(SlotHelp)] int slot = 0) => clients.Close(slot);

    [McpServerTool(Name = "client_screenshot", ReadOnly = true)]
    [Description("A picture of the slot's client window, taken after every command sent before it has been played. " +
                 "The picture may be scaled down: the text says by how much, to turn a point on it into window pixels. " +
                 "Give a region (x, y, width, height in window pixels) for only that part, enlarged by zoom; " +
                 "savePath keeps a copy of the picture (a PR's screenshot).")]
    public static async Task<IEnumerable<ContentBlock>> Screenshot(GameClients clients,
        [Description("The widest the picture may be, in pixels (the whole window).")] int maxWidth = 1600,
        [Description("Region's left edge, window pixels.")] int x = 0,
        [Description("Region's top edge, window pixels.")] int y = 0,
        [Description("Region's width; 0 for the whole window.")] int width = 0,
        [Description("Region's height; 0 for the whole window.")] int height = 0,
        [Description("How much a region is enlarged (1 to 4).")] double zoom = 2,
        [Description("Also write the picture to this file (.jpg).")] string? savePath = null,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
    {
        bool region = width > 0 && height > 0;
        zoom = Math.Clamp(zoom, 1, 4);
        string command = region
            ? FormattableString.Invariant($"shot {Math.Clamp((int)Math.Round(width * zoom), 50, 4096)} {x} {y} {width} {height}")
            : $"shot {Math.Clamp(maxWidth, 200, 4096)}";
        string[] reply = (await clients.SendAsync(slot, command, TimeSpan.FromSeconds(20), cancellationToken)).Split(' ');
        // "path width height sourceWidth sourceHeight [regionX regionY]"; the path may hold spaces.
        int numbers = region ? 6 : 4, n = reply.Length;
        if (n <= numbers) throw new ModelContextProtocol.McpException("The client's mod is older than the tools: relaunch the client to update it.");
        string path = string.Join(' ', reply[..(n - numbers)]);
        int Number(int fromEnd) => int.Parse(reply[n - fromEnd], CultureInfo.InvariantCulture);
        int pictureWidth = Number(numbers), pictureHeight = Number(numbers - 1), sourceWidth = Number(numbers - 2), sourceHeight = Number(numbers - 3);
        byte[] picture = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!string.IsNullOrWhiteSpace(savePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(savePath))!);
            await File.WriteAllBytesAsync(savePath, picture, cancellationToken);
        }
        double scale = (double)sourceWidth / pictureWidth;
        string text = region
            ? FormattableString.Invariant($"Picture {pictureWidth}x{pictureHeight} of the window's region at ({Number(2)}, {Number(1)}), ") +
              FormattableString.Invariant($"{sourceWidth}x{sourceHeight}: a point (px, py) on it is window ({Number(2)} + px*{scale:0.###}, {Number(1)} + py*{scale:0.###}).")
            : $"Picture {pictureWidth}x{pictureHeight} of a {sourceWidth}x{sourceHeight} window" +
              (Math.Abs(scale - 1) < 0.001 ? "." : FormattableString.Invariant($": multiply a point on it by {scale:0.###} to click it."));
        if (!string.IsNullOrWhiteSpace(savePath)) text += $" Saved to {savePath}.";
        return new ContentBlock[]
        {
            ImageContentBlock.FromBytes(picture, "image/jpeg"),
            new TextContentBlock { Text = text },
        };
    }

    [McpServerTool(Name = "client_click")]
    [Description("Clicks at a window point with the mod's own mouse (the computer's pointer is not moved).")]
    public static Task<string> Click(GameClients clients,
        [Description("X in window pixels.")] int x,
        [Description("Y in window pixels, from the top.")] int y,
        [Description("Right button instead of left.")] bool right = false,
        [Description("How many clicks (2: a double click).")] int times = 1,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
        => Done(clients.SendAsync(slot, $"click {x} {y}{(right ? " right" : "")} {Math.Clamp(times, 1, 5)}", cancellationToken: cancellationToken));

    [McpServerTool(Name = "client_move")]
    [Description("Moves the mod's mouse to a window point (a hover: tooltips).")]
    public static Task<string> Move(GameClients clients, int x, int y, [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => Done(clients.SendAsync(slot, $"move {x} {y}", cancellationToken: cancellationToken));

    [McpServerTool(Name = "client_drag")]
    [Description("Drags with the left button from one window point to another (an item to a slot, a window by its bar).")]
    public static Task<string> Drag(GameClients clients, int fromX, int fromY, int toX, int toY,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => Done(clients.SendAsync(slot, $"drag {fromX} {fromY} {toX} {toY}", cancellationToken: cancellationToken));

    [McpServerTool(Name = "client_scroll")]
    [Description("Scrolls the wheel at a window point: positive notches up, negative down.")]
    public static Task<string> Scroll(GameClients clients, int x, int y, int notches,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => Done(clients.SendAsync(slot, $"scroll {x} {y} {notches}", cancellationToken: cancellationToken));

    [McpServerTool(Name = "client_key")]
    [Description("Presses a key or a chord: \"enter\", \"esc\", \"ctrl+tab\", \"f10\", \"a\", \"1\" -- any name of Unity's Key enum.")]
    public static Task<string> Key(GameClients clients, [Description("The key or keys joined with +.")] string keys,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => Done(clients.SendAsync(slot, $"key {keys.Replace(" ", "")}", cancellationToken: cancellationToken));

    [McpServerTool(Name = "client_type")]
    [Description("Types text into whatever field has the focus (click it first). Chat commands too: click the chat, " +
                 "type \".item 39 1\", then client_key enter.")]
    public static Task<string> Type(GameClients clients, [Description("The text.")] string text,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => Done(clients.SendAsync(slot, $"type {text.Replace('\n', ' ')}", TimeSpan.FromSeconds(60), cancellationToken));

    [McpServerTool(Name = "client_ui", ReadOnly = true)]
    [Description("The texts the client's interface shows, each with its box (x, y, w, h in window pixels), element name " +
                 "(n) and kind (k); only those containing the filter (case and accents aside) when one is given. Cheaper " +
                 "and more exact than a screenshot for reading and for finding where to click.")]
    public static Task<string> Ui(GameClients clients, [Description("Only texts containing this.")] string? filter = null,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => clients.SendAsync(slot, $"ui {filter ?? ""}".TrimEnd(), cancellationToken: cancellationToken);

    [McpServerTool(Name = "client_find", ReadOnly = true)]
    [Description("The interface elements whose name or kind contains the query, with the texts inside each and its box. " +
                 "hidden=true finds the hidden ones too, marked \"hidden\": true -- an error label a field keeps hidden, " +
                 "a window not opened yet.")]
    public static Task<string> Find(GameClients clients, [Description("Part of an element's name or kind, e.g. \"Button\".")] string query,
        [Description("Hidden elements too.")] bool hidden = false,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => clients.SendAsync(slot, hidden ? $"find -a {query}" : $"find {query}", cancellationToken: cancellationToken);

    [McpServerTool(Name = "client_set_text")]
    [Description("Writes a text into the field named `element` (the element itself or the first text field inside it), " +
                 "as typing it would end: its value set and its change told, so the screen takes it -- the creation " +
                 "screen's name (input_name), the chat's line (ti_chatInput). More reliable than client_type, whose keys " +
                 "the client's fields drop in the background. submit=true validates it as Enter would: the chat sends " +
                 "the line (a chat command too, \".item 39 1\").")]
    public static Task<string> SetText(GameClients clients,
        [Description("The element's exact name (client_find gives it), e.g. \"input_name\" or \"ti_chatInput\".")] string element,
        [Description("The text.")] string text,
        [Description("Validate it afterwards, as Enter would (send the chat line).")] bool submit = false,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => clients.SendAsync(slot, $"settext{(submit ? "!" : "")} {element.Trim()} {text.Replace('\n', ' ')}", cancellationToken: cancellationToken);

    [McpServerTool(Name = "client_log", ReadOnly = true)]
    [Description("The client's mod log (MelonLoader's Latest.log): what the loaded mods wrote and their " +
                 "errors. Lines are numbered; pass the lastLine of the previous call as `since` to read only what is new.")]
    public static string Log(GameClients clients,
        [Description("Only lines after this one (0: from the start).")] int since = 0,
        [Description("Only lines containing this text, case aside.")] string? contains = null,
        [Description("At most this many lines, the newest.")] int max = 100)
        => clients.ReadLog(since, contains, Math.Clamp(max, 1, 2000));

    [McpServerTool(Name = "client_pick", ReadOnly = true)]
    [Description("The interface element under a window point and its parents, innermost first: what a click there hits.")]
    public static Task<string> Pick(GameClients clients, int x, int y,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => clients.SendAsync(slot, $"pick {x} {y}", cancellationToken: cancellationToken);

    private static async Task<string> Done(Task<string> sent)
    {
        await sent;
        return "ok";
    }
}
