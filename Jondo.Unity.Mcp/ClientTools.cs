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
                 "The picture may be scaled down: the text says by how much, to turn a point on it into window pixels.")]
    public static async Task<IEnumerable<ContentBlock>> Screenshot(GameClients clients,
        [Description("The widest the picture may be, in pixels.")] int maxWidth = 1600,
        [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
    {
        string[] reply = (await clients.SendAsync(slot, $"shot {Math.Clamp(maxWidth, 200, 4096)}", TimeSpan.FromSeconds(20), cancellationToken)).Split(' ');
        // "path width height windowWidth windowHeight"; the path may hold spaces.
        int n = reply.Length;
        string path = string.Join(' ', reply[..(n - 4)]);
        int width = int.Parse(reply[n - 4], CultureInfo.InvariantCulture), windowWidth = int.Parse(reply[n - 2], CultureInfo.InvariantCulture);
        byte[] picture = await File.ReadAllBytesAsync(path, cancellationToken);
        double scale = (double)windowWidth / width;
        return new ContentBlock[]
        {
            ImageContentBlock.FromBytes(picture, "image/jpeg"),
            new TextContentBlock
            {
                Text = $"Picture {reply[n - 4]}x{reply[n - 3]} of a {reply[n - 2]}x{reply[n - 1]} window" +
                       (Math.Abs(scale - 1) < 0.001 ? "." : $": multiply a point on it by {scale:0.###} to click it."),
            },
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
    [Description("The interface elements whose name or kind contains the query, with the texts inside each and its box.")]
    public static Task<string> Find(GameClients clients, [Description("Part of an element's name or kind, e.g. \"Button\".")] string query,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => clients.SendAsync(slot, $"find {query}", cancellationToken: cancellationToken);

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
