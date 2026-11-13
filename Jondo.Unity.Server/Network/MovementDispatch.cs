using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network;

/// <summary>Roleplay and combat movement routing for requests already decoded by GameNodeProxy.</summary>
internal static class MovementDispatch
{
    // Called under the owner's session turn. Intentional combat no-ops also claim the request.
    internal static async Task<bool> TryHandleAsync(GameSession session, GameFrame? frame, Action? onMapChange = null)
    {
        if (frame?.Uid is null || frame.Content is not (MapMovementRequest or MapMovementCancelRequest
            or MapMovementConfirmRequest or MapChangeRequest))
            return false;
        if (!session.IsAuthenticated || !session.HasCharacter || !session.IsInWorld) return false;

        using var context = SessionContext.Push(session);
        // Decide the context once. Arrival hooks may enter a fight, and must not redispatch this packet.
        if (session.State.IsInFight)
        {
            if (frame.Content is MapMovementRequest movement)
                await FightHandler.HandleMovementAsync(session, movement);
            // Combat acknowledges its action sequences, not roleplay confirmation/cancellation.
            return true;
        }

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
            case MapChangeRequest request:
                onMapChange?.Invoke();
                await WorldMoveHandler.ChangeMapAsync(session, request);
                break;
        }
        return true;
    }
}
