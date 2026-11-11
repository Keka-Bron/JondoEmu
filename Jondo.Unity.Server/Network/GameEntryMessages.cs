using Google.Protobuf;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;

namespace Jondo.Unity.Server.Network;

/// <summary>
/// Transitional projection for legacy entry builders.
/// An existing packet may use a stable send only when the projection preserves
/// its entire protobuf payload, including nested fields and unknown fields.
/// </summary>
internal static class GameEntryMessages
{
    private static readonly GameCodec Wire = new();

    internal static bool TryProject(byte[] packet, out GameEvent? message)
    {
        message = null;
        try
        {
            var envelope = Wire.ReadEnvelope(packet);
            if (envelope.Kind != GameMessageKind.Event || GameBindings.StableName(envelope.Alias) == null)
                return false;
            var decoded = Wire.Decode(packet);
            if (decoded.Content is not GameEvent candidate) return false;
            var encoded = Wire.ReadEnvelope(Wire.Encode(decoded));
            // Compare parsed protobuf values, not serialization order or omitted
            // scalar defaults. Unknown/nested fields and message presence matter.
            if (encoded.Kind != envelope.Kind || encoded.Uid != envelope.Uid
                || encoded.Content.TypeUrl != envelope.Content.TypeUrl
                || !Wire.DecodeRawPayload(envelope.Alias, envelope.Content.Value.Span)
                    .Equals(Wire.DecodeRawPayload(encoded.Alias, encoded.Content.Value.Span))) return false;
            // Envelope extensions are also outside the stable contract.
            var originalEnvelope = Wire.DecodeWire(packet);
            var restoredEnvelope = Wire.DecodeWire(Wire.Encode(decoded));
            // Normalize only serialization differences inside Any; retain all
            // envelope extensions for the structural equality check.
            NormalizeAny(originalEnvelope, encoded.Content);
            if (!originalEnvelope.Equals(restoredEnvelope)) return false;
            message = candidate;
            return true;
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or GameConversionException)
        {
            return false;
        }
    }

    private static void NormalizeAny(IMessage message, Google.Protobuf.WellKnownTypes.Any content)
    {
        if (message is Google.Protobuf.WellKnownTypes.Any any)
        {
            any.Value = content.Value;
            return;
        }
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
            if (field.FieldType == Google.Protobuf.Reflection.FieldType.Message && !field.IsRepeated
                && field.Accessor.GetValue(message) is IMessage child)
                NormalizeAny(child, content);
    }

    internal static Task SendAsync(GameSession session, byte[] packet)
        => TryProject(packet, out var message) ? session.SendAsync(message!) : session.SendAsync(packet);

    internal static Task SendAsync(System.Net.Sockets.NetworkStream stream, byte[] packet)
    {
        var session = SessionContext.Actual;
        return session != null && ReferenceEquals(session.Stream, stream)
            ? SendAsync(session, packet)
            : Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, packet);
    }
}
