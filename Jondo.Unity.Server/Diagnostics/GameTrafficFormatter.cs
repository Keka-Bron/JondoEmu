using System.Text.Json;
using System.Text.Json.Serialization;
using Jondo.Unity.Protocol.Game.Wire;

namespace Jondo.Unity.Server.Diagnostics;

/// <summary>A diagnostic projection only: no converted object is passed to game dispatch.</summary>
internal static class GameTrafficFormatter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 64
    };
    internal const int MaxLiveFields = 2048;

    /// <summary>Also returns a reviewed name when typed conversion fails. Caller supplies scrubbed bytes.</summary>
    internal static bool TryFormat(byte[] frame, out string name, out string fields)
    {
        name = fields = "";
        try
        {
            var envelope = GameBindings.ReadEnvelope(frame);
            name = GameBindings.StableName(envelope.Alias) ?? "";
            if (name.Length == 0) return false;
            var message = GameBindings.Decode(frame);
            string json = GameBindings.SerializePayload(message.Content, Json);
            fields = (message.Uid is int uid ? $"uid={uid} " : "") + json;
            if (fields.Length > MaxLiveFields) fields = fields[..MaxLiveFields] + "…";
            return true;
        }
        catch (Exception)
        {
            // Logging must survive malformed packets, incomplete mappings and formatter failures.
            fields = "";
            return false;
        }
    }
}
