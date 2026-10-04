using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Jondo.Unity.Cytrus;

/// <summary>
/// A SHA-1 digest held by value.
/// </summary>
/// <remarks>
/// Every name in a Cytrus manifest is a SHA-1: files, chunks and bundles. The 4K pack alone lists
/// a few hundred thousand chunks, twice (once per file, once per bundle), so they are kept as
/// three integers instead of forty-character strings: no allocation per hash, and equality and
/// hashing come for free with the record.
/// </remarks>
public readonly record struct Sha1Hash(ulong High, ulong Middle, uint Low)
{
    /// <summary>The length of a SHA-1 digest in bytes.</summary>
    public const int Length = 20;

    /// <summary>True for the default value, which a manifest uses for "no hash".</summary>
    public bool IsEmpty => High == 0 && Middle == 0 && Low == 0;

    public static Sha1Hash FromBytes(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != Length)
            throw new ArgumentException("a SHA-1 digest is " + Length + " bytes, got " + digest.Length, nameof(digest));

        return new Sha1Hash(
            BinaryPrimitives.ReadUInt64BigEndian(digest),
            BinaryPrimitives.ReadUInt64BigEndian(digest[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(digest[16..]));
    }

    /// <summary>The SHA-1 of some bytes.</summary>
    public static Sha1Hash Of(ReadOnlySpan<byte> data)
    {
        Span<byte> digest = stackalloc byte[Length];
        SHA1.HashData(data, digest);
        return FromBytes(digest);
    }

    /// <summary>Reads the usual forty hexadecimal digits, in either case.</summary>
    public static Sha1Hash Parse(string hex) => FromBytes(Convert.FromHexString(hex));

    public void CopyTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, High);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], Middle);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], Low);
    }

    /// <summary>Lowercase hexadecimal, which is how the CDN names its bundles.</summary>
    public override string ToString()
    {
        Span<byte> digest = stackalloc byte[Length];
        CopyTo(digest);
        return Convert.ToHexStringLower(digest);
    }
}
