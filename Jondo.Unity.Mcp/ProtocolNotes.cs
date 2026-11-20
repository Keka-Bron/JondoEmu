using System.Text;
using System.Text.Json;
using ModelContextProtocol;

namespace Jondo.Unity.Mcp;

/// <summary>
/// docs/protocol_fields.json: what each field of a game message means, where it has been settled.
/// The client's names are rotated (ind { fqfe, fqff, fqfg }), so a field's meaning is a finding,
/// kept with how it was found -- the withdrawn invitation's ind went out with its two characters
/// the wrong way round for months, its order read off the client and never checked (#216).
/// </summary>
internal static class ProtocolNotes
{
    public const string File = "docs/protocol_fields.json";

    public static string Describe(string source, string? op)
    {
        string path = Path.Combine(source, File.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(path)) throw new McpException($"No {File} in {source}.");
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("messages", out var messages)) throw new McpException($"{File} has no \"messages\".");
        var text = new StringBuilder();
        foreach (var message in messages.EnumerateObject())
        {
            if (!string.IsNullOrWhiteSpace(op) && !message.Name.Equals(op.Trim(), StringComparison.Ordinal)) continue;
            var m = message.Value;
            text.Append(message.Name);
            if (m.TryGetProperty("name", out var name)) text.Append($" ({name.GetString()})");
            if (m.TryGetProperty("direction", out var direction)) text.Append($", {direction.GetString()}");
            text.AppendLine(":");
            if (m.TryGetProperty("fields", out var fields))
                foreach (var field in fields.EnumerateObject())
                    text.AppendLine($"  {field.Name}: {field.Value.GetString()}");
            if (m.TryGetProperty("settled", out var settled)) text.AppendLine($"  settled: {settled.GetString()}");
        }
        if (text.Length == 0)
            return string.IsNullOrWhiteSpace(op)
                ? $"{File} lists no message yet."
                : $"Nothing settled about {op} yet: once its fields are known (a capture, the client's code, a fix checked in the client), add it to {File}.";
        return text.ToString().TrimEnd();
    }
}
