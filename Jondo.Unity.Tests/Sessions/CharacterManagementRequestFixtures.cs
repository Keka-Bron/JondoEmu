using System.Text;

namespace Jondo.Unity.Tests.Sessions;

/// <summary>
/// Independent fixtures for the still-legacy request boundary. Refresh assertions
/// use semantic frames and opaque codec tokens, without a production wire encoder.
/// </summary>
internal static class CharacterManagementRequestFixtures
{
    internal static byte[] Create(string name)
        => Request("kvz", Message(0x0a, [.. Message(0x0a, Encoding.UTF8.GetBytes(name)), 0x30, 1, 0x38, 18]));

    internal static byte[] Delete(long characterId)
        => Request("kvu", [0x08, .. VarInt(checked((ulong)characterId))]);

    internal static byte[] CreationAccepted
        => Message(0x0a, Message(0x0a, Message(0x0a, Encoding.ASCII.GetBytes("type.ankama.com/kvb"))));

    private static byte[] Request(string alias, byte[] body)
    {
        byte[] content = [.. Message(0x0a, Encoding.ASCII.GetBytes("type.ankama.com/" + alias)), .. Message(0x12, body)];
        // Request envelope with an explicit zero UID; no generated protobuf types.
        return Message(0x12, [.. Message(0x0a, content), 0x10, 0]);
    }

    private static byte[] Message(byte tag, byte[] body)
        => [tag, .. VarInt((ulong)body.Length), .. body];

    private static IEnumerable<byte> VarInt(ulong value)
    {
        do
        {
            byte next = (byte)(value & 0x7f);
            value >>= 7;
            yield return (byte)(next | (value == 0 ? 0 : 0x80));
        } while (value != 0);
    }
}
