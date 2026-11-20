using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Jondo.Unity.Mcp;

/// <summary>
/// The hero mode of a test slot's account, as the heroes window drives it (/api/heroes/*): the
/// account's characters, the ones connected as heroes, connecting, letting go and taking one in
/// hand. The routes want the account's launcher token, asked for here once per slot.
/// </summary>
/// <remarks>
/// To act AS a hero without taking him in hand -- his quests, his bag --, give «hero» (his id or
/// name) to test_call's body, or to game_state and game_give.
/// </remarks>
[McpServerToolType]
public static class HeroTools
{
    private const string SlotHelp = "The test slot: 0 is the jondoplay account, n is jondoplayNN.";
    private const string WhoHelp = "The character: his name or his id.";

    private static readonly ConcurrentDictionary<int, string> Tokens = new();

    [McpServerTool(Name = "hero_list", ReadOnly = true)]
    [Description("The slot's account's characters as the heroes window shows them: who is played, who is connected " +
                 "as a hero, whether he shares quests or passes his turns, and the quests he is behind in when the server says.")]
    public static async Task<string> List(Emulator emulator, [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(await CallAsync(emulator, "heroes/list", new JsonObject(), slot, cancellationToken));
        if (!doc.RootElement.TryGetProperty("characters", out var characters)) return doc.RootElement.GetRawText();
        var text = new StringBuilder();
        foreach (var c in characters.EnumerateArray())
        {
            string Get(string name) => c.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText() : "";
            text.Append($"{Get("name")} ({Get("id")}) level {Get("level")}");
            if (Get("master") == "true") text.Append(", played");
            if (Get("hero") == "true") text.Append(", hero");
            if (Get("questSync") == "true") text.Append(", shares quests");
            if (Get("autoPass") == "true") text.Append(", passes his turns");
            if (Get("busy") == "true") text.Append(", busy elsewhere");
            if (c.TryGetProperty("questLag", out var lag) && lag.ValueKind == JsonValueKind.Array && lag.GetArrayLength() > 0)
                text.Append(", behind in ").Append(lag.GetRawText());
            text.AppendLine();
        }
        return text.Length > 0 ? text.ToString().TrimEnd() : doc.RootElement.GetRawText();
    }

    [McpServerTool(Name = "hero_connect")]
    [Description("Connects a character of the slot's account as a hero beside the one played (the window's CONNECTER).")]
    public static async Task<string> Connect(Emulator emulator, [Description(WhoHelp)] string character,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => await CallAsync(emulator, "heroes/connect", new JsonObject { ["id"] = await IdOfAsync(emulator, character, slot, cancellationToken) }, slot, cancellationToken);

    [McpServerTool(Name = "hero_disconnect")]
    [Description("Lets a hero go (the window's DÉCONNECTER).")]
    public static async Task<string> Disconnect(Emulator emulator, [Description(WhoHelp)] string character,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => await CallAsync(emulator, "heroes/disconnect", new JsonObject { ["id"] = await IdOfAsync(emulator, character, slot, cancellationToken) }, slot, cancellationToken);

    [McpServerTool(Name = "hero_switch")]
    [Description("Takes a hero in hand in the same connection: he is played, and the one played before becomes his hero. " +
                 "The test routes then act on him (session, quests, bag).")]
    public static async Task<string> Switch(Emulator emulator, [Description(WhoHelp)] string character,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => await CallAsync(emulator, "heroes/switch", new JsonObject { ["id"] = await IdOfAsync(emulator, character, slot, cancellationToken) }, slot, cancellationToken);

    /// <summary>A heroes route with the slot's token, asked for again when the server no longer knows it.</summary>
    private static async Task<string> CallAsync(Emulator emulator, string route, JsonObject body, int slot, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (!Tokens.TryGetValue(slot, out string? token))
            {
                token = Emulator.Field(await emulator.TestAsync("token", slot: slot, cancellationToken: cancellationToken), "token");
                Tokens[slot] = token;
            }
            var sent = body.DeepClone().AsObject();
            sent["token"] = token;
            try
            {
                return await emulator.CallAsync(route, sent, cancellationToken);
            }
            catch (McpException ex) when (attempt == 0 && ex.Message.StartsWith("HTTP 401", StringComparison.Ordinal))
            {
                Tokens.TryRemove(slot, out _);
            }
        }
    }

    /// <summary>A character of the slot's account, by id or by name.</summary>
    private static async Task<long> IdOfAsync(Emulator emulator, string character, int slot, CancellationToken cancellationToken)
    {
        if (long.TryParse(character, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) return id;
        using var doc = JsonDocument.Parse(await emulator.TestAsync("characters", slot: slot, cancellationToken: cancellationToken));
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            if (c.TryGetProperty("Name", out var name) && string.Equals(name.GetString(), character, StringComparison.OrdinalIgnoreCase))
                return c.GetProperty("Id").GetInt64();
        }
        throw new McpException($"The slot {slot} account has no character named {character}.");
    }
}
