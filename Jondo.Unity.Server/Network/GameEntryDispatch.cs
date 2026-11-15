using Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Network;

/// <summary>Entry lifecycle eligibility and ticket binding; decoding belongs to GameNodeProxy.</summary>
internal static class GameEntryDispatch
{
    internal static bool IsAllowed(GameSession session, GameRequest request) => request switch
    {
        IdentificationRequest => !session.IsAuthenticated,
        PingRequest => session.IsAuthenticated,
        CharacterListRequest or CharacterSelectionRequest or CharacterFirstSelectionRequest
            or CharacterForceSelectionReadyRequest
            => session.IsAuthenticated && !session.IsInWorld,
        _ => session.IsAuthenticated && session.HasCharacter && session.IsInWorld,
    };

    internal static bool BindTicket(GameSession session, IdentificationRequest request)
    {
        if (session.IsAuthenticated || string.IsNullOrWhiteSpace(request.TicketKey)) return false;
        var ticket = SessionRegistry.Redeem(request.TicketKey);
        if (ticket == null) return false;
        // The launch owns the language, as it does in the Connection backend.
        session.BindAccount(ticket.AccountId, ticket.ServerId, ticket.Language);
        return true;
    }
}
