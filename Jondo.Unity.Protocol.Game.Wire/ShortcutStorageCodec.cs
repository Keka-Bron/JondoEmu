namespace Jondo.Unity.Protocol.Game.Wire;

/// <summary>Reads a historical stored shortcut without constructing a Game envelope or bar event.</summary>
public static class ShortcutStorageCodec
{
    public static Shortcut Decode(ReadOnlySpan<byte> payload)
        => GameBindings.ToStable(Messages.lhs.Parser.ParseFrom(payload));
}
