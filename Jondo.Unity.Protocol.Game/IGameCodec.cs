namespace Jondo.Unity.Protocol.Game;

public abstract record GamePayload;
public abstract record GameRequest : GamePayload;
public abstract record GameEvent : GamePayload;
public abstract record GameResponse : GamePayload;
public enum GameMessageKind { Request, Response, Event }

/// <summary>Unframed Game message. Request/response correlation IDs are signed; events have no ID.</summary>
public sealed record GameFrame(GamePayload Content, int? Uid = null);

public interface IGameCodec
{
    GameFrame Decode(ReadOnlySpan<byte> payload);
    byte[] Encode(GameFrame message);
}

public sealed class GameConversionException(string message) : Exception(message);
