using Google.Protobuf;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network;

/// <summary>The first typed request slice. Unrelated messages and combat retain their current dispatch.</summary>
internal static class WorldMovementDispatch
{
    // Called under the packet loop's session turn. A recognized invalid request is
    // consumed here: never reinterpret it using a permissive legacy field parser.
    internal static async Task<bool> TryHandleAsync(GameSession session, byte[] payload, string typeUrl)
    {
        if (session.State.IsInFight || !typeUrl.StartsWith(ConnectionProtocol.UriPrefix, StringComparison.Ordinal))
            return false;
        string? name = GameBindings.StableName(typeUrl[ConnectionProtocol.UriPrefix.Length..]);
        if (name is not (nameof(MapMovementRequest) or nameof(MapMovementCancelRequest) or nameof(MapMovementConfirmRequest)))
            return false;

        if (!session.IsAuthenticated || !session.HasCharacter || !session.IsInWorld) return true;
        GameFrame frame;
        try
        {
            frame = session.Decode(payload);
            bool matches = (name, frame.Content) is
                (nameof(MapMovementRequest), MapMovementRequest) or
                (nameof(MapMovementCancelRequest), MapMovementCancelRequest) or
                (nameof(MapMovementConfirmRequest), MapMovementConfirmRequest);
            if (frame.Uid is null || !matches)
                throw new GameConversionException("Movement request does not match its envelope.");
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or GameConversionException)
        {
            Program.LogDebug($"[Move] Invalid {name}: {ex.Message}");
            return true;
        }

        using var context = SessionContext.Push(session);
        switch (frame.Content)
        {
            case MapMovementRequest request:
                if (session.Stream is { } movingStream) await SlotLookHandler.OnMovingAsync(movingStream);
                await WorldMoveHandler.ConfirmMovementAsync(session, request);
                break;
            case MapMovementCancelRequest request:
                WorldMoveHandler.StopMovement(session, request);
                break;
            case MapMovementConfirmRequest:
                await WorldMoveHandler.AllowMapExitAsync(session, frame.Uid.Value);
                // Preserve the existing after-arrival party/fight joining notification.
                if (session.Stream is { } arrivedStream) await FightHandler.AfterWalkAsync(arrivedStream);
                break;
        }
        return true;
    }
}
