using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jondo.Unity.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Heroes.Mcp;

[McpServerToolType]
public static class HeroTools
{
    private const string SlotHelp = "The test slot: 0 is the jondoplay account, n is jondoplayNN.";
    private const string CharacterHelp = "The character's name or ID.";

    [McpServerTool(Name = "hero_list", ReadOnly = true)]
    [Description("Lists the slot account's characters, including the played character, connected heroes, quest sharing, and automatic turn passing.")]
    public static async Task<string> List(IMcpTestClient client, [Description(SlotHelp)] int slot = 0,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(await client.PlayerAsync("heroes/list", slot: slot,
            cancellationToken: cancellationToken));
        if (!document.RootElement.TryGetProperty("characters", out var characters))
            return document.RootElement.GetRawText();

        var text = new StringBuilder();
        foreach (var character in characters.EnumerateArray())
        {
            text.Append($"{Field(character, "name")} ({Field(character, "id")}) level {Field(character, "level")}");
            if (Flag(character, "master")) text.Append(", played");
            if (Flag(character, "hero")) text.Append(", hero");
            if (Flag(character, "questSync")) text.Append(", shares quests");
            if (Flag(character, "autoPass")) text.Append(", passes his turns");
            if (Flag(character, "busy")) text.Append(", busy elsewhere");
            if (character.TryGetProperty("questLag", out var lag) && lag.ValueKind == JsonValueKind.Array && lag.GetArrayLength() > 0)
                text.Append(", behind in ").Append(lag.GetRawText());
            text.AppendLine();
        }
        return text.Length > 0 ? text.ToString().TrimEnd() : document.RootElement.GetRawText();
    }

    [McpServerTool(Name = "hero_connect")]
    [Description("Connects a character of the slot's account as a hero beside the played character.")]
    public static Task<string> Connect(IMcpTestClient client, [Description(CharacterHelp)] string character,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => SendAsync(client, "heroes/connect", character, slot, cancellationToken);

    [McpServerTool(Name = "hero_disconnect")]
    [Description("Disconnects a hero from the slot's account.")]
    public static Task<string> Disconnect(IMcpTestClient client, [Description(CharacterHelp)] string character,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => SendAsync(client, "heroes/disconnect", character, slot, cancellationToken);

    [McpServerTool(Name = "hero_switch")]
    [Description("Plays a hero on the same connection and makes the previously played character a hero. Test routes then act on the new played character.")]
    public static Task<string> Switch(IMcpTestClient client, [Description(CharacterHelp)] string character,
        [Description(SlotHelp)] int slot = 0, CancellationToken cancellationToken = default)
        => SendAsync(client, "heroes/switch", character, slot, cancellationToken);

    private static async Task<string> SendAsync(IMcpTestClient client, string route, string character, int slot,
        CancellationToken cancellationToken)
    {
        long id = await CharacterIdAsync(client, character, slot, cancellationToken);
        return await client.PlayerAsync(route, new JsonObject { ["id"] = id }, slot, cancellationToken);
    }

    private static async Task<long> CharacterIdAsync(IMcpTestClient client, string character, int slot,
        CancellationToken cancellationToken)
    {
        if (long.TryParse(character, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) return id;

        using var document = JsonDocument.Parse(await client.TestAsync("characters", slot: slot,
            cancellationToken: cancellationToken));
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (string.Equals(Field(item, "Name"), character, StringComparison.OrdinalIgnoreCase))
                return item.GetProperty("Id").GetInt64();
        }
        throw new McpException($"The slot {slot} account has no character named {character}.");
    }

    private static string Field(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) ? value.ToString() : "";

    private static bool Flag(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
