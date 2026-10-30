using Jondo.Unity.Protocol.Connection;

namespace Jondo.Unity.Server.Network.Connection;

internal interface IConnectionBackend
{
    long ResolveAccount(string token);
    IdentificationSuccess Identify(long accountId);
    bool IsServerJoinable(int serverId);
    SelectServerSuccess IssueTicket(long accountId, int serverId);
}

internal abstract record ConnectionAction
{
    public sealed record Reply(ConnectionResponse Response, bool CloseAfterReply = false) : ConnectionAction;
    public sealed record Ignore : ConnectionAction;
    public sealed record Close : ConnectionAction;
}

/// <summary>One instance per connection. Only stable protocol types enter the service.</summary>
internal sealed class ConnectionService(IConnectionBackend backend)
{
    internal const int MaxCharactersPerServer = 5;
    private long _accountId;

    public ConnectionAction Handle(ConnectionMessage message)
    {
        if (message is not ConnectionRequest request) return new ConnectionAction.Ignore();

        switch (request.Content)
        {
            case Ping:
                return Reply(new Pong());
            case IdentificationRequest { Method: TokenIdentification token }:
                _accountId = string.IsNullOrWhiteSpace(token.Token) ? 0 : backend.ResolveAccount(token.Token);
                return _accountId > 0
                    ? Reply(new IdentificationResponse(backend.Identify(_accountId)))
                    : new ConnectionAction.Close();
            case IdentificationRequest:
                // The launcher authenticates with a token; login-based identification is
                // represented by the protocol but is not implemented by this backend.
                return new ConnectionAction.Close();
            case SelectServerRequest selection:
                if (_accountId <= 0 || !backend.IsServerJoinable(selection.ServerId))
                    return new ConnectionAction.Close();
                return Reply(new SelectServerResponse(backend.IssueTicket(_accountId, selection.ServerId)), close: true);
            default:
                return new ConnectionAction.Ignore();
        }

        ConnectionAction Reply(ResponseContent content, bool close = false)
            => new ConnectionAction.Reply(new ConnectionResponse(request.CorrelationId, content), close);
    }
}
