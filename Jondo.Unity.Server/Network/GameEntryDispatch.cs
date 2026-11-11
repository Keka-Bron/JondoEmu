using Google.Protobuf;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;

namespace Jondo.Unity.Server.Network;

/// <summary>Typed input boundary for entry, called under the owning session's turn.</summary>
internal static class GameEntryDispatch
{
    // A null result means this adapter does not own the request. Malformed owned
    // requests throw: they must never reach the permissive legacy field readers.
    internal static GameFrame? Decode(GameSession session, byte[] payload, string typeUrl)
    {
        if (!typeUrl.StartsWith(ConnectionProtocol.UriPrefix, StringComparison.Ordinal)) return null;
        string? name = GameBindings.StableName(typeUrl[ConnectionProtocol.UriPrefix.Length..]);
        if (name is not (nameof(IdentificationRequest) or nameof(CharacterListRequest)
            or nameof(CharacterSelectionRequest) or nameof(CharacterFirstSelectionRequest)
            or nameof(SequenceNumberRequest) or nameof(ContextCreationRequest)
            or nameof(ContextReadyRequest) or nameof(MapInformationRequest) or nameof(PingRequest)
            or nameof(FightMapRequest) or nameof(CharacterForceSelectionReadyRequest)
             or nameof(GuildWeeklyResetDateRequest) or nameof(ChatChannelSubscriptionRequest))) return null;

        var frame = session.Decode(payload);
        if (frame.Uid is null || frame.Content is not GameRequest request || request.GetType().Name != name)
            throw new GameConversionException("Entry request does not match its envelope.");
        return frame;
    }

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
