using Google.Protobuf;

namespace Jondo.Unity.Server.Managers;

/// <summary>
/// Reads the historical GuildRanks.Rights storage format: packed protobuf int32s,
/// without a field tag or length. Decode the entire blob before returning any rights.
/// This preserves order and duplicates for delivery and rejects truncated permissions.
/// </summary>
internal static class GuildRankRights
{
    internal static int[] Decode(byte[]? packed)
    {
        if (packed == null || packed.Length == 0) return [];
        using var input = new CodedInputStream(packed);
        var rights = new List<int>();
        while (!input.IsAtEnd) rights.Add(input.ReadInt32());
        return rights.ToArray();
    }
}
