using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Jondo.Unity.Reversing;

/// <summary>
/// The protocol descriptor, taken from the client itself.
///
/// This is the foundation of everything else. Without a complete descriptor there are no fingerprints, and without fingerprints there is
/// no way to match one version's messages with the next one's when Ankama
/// rotates their three-letter names.
///
/// Nothing has to be deobfuscated nor the game started: protobuf needs the descriptor to
/// work, so the client carries it inside. It is in global-metadata.dat, which is where IL2CPP
/// keeps the literals, and it is RAW: the FileDescriptorProto's bytes as they are, not in base64
/// as the desktop C# generator leaves them. It is recognised because a descriptor's field 1
/// is the file's name and there one reads in the clear «com.ankama.dofus.proto», «network.proto».
///
/// ─── Why it is checked with a round trip ────────────────────────────────────────────────
///
/// A protobuf reader is VERY permissive: almost any heap of bytes lets itself be parsed without
/// complaining, leaving what it does not understand in unknown fields. So "it parsed" proves
/// nothing. What does prove it is that serialising it again gives the SAME bytes: that only happens
/// if every input byte fell into a field the descriptor knows, and it is what decides where
/// the block ends, which is the datum written nowhere.
/// </summary>
public static class DescriptorExtractor
{
    public sealed record Blob(int Offset, int Length, FileDescriptorProto File);

    /// <summary>The descriptors inside the client's metadata file.</summary>
    public static List<Blob> FindIn(string metadataPath)
    {
        byte[] data = File.ReadAllBytes(metadataPath);
        var blobs = new List<Blob>();
        int lastEnd = 0;

        foreach (int start in Starts(data))
        {
            if (start < lastEnd) continue;   // it was already inside the previous one

            var blob = ReadAt(data, start);
            if (blob == null) continue;

            blobs.Add(blob);
            lastEnd = blob.Offset + blob.Length;
        }

        return blobs;
    }

    /// <summary>
    /// Where one can start: at the 0x0A of field 1, followed by the name's length and a
    /// name ending in «.proto». It is searched for by the end —the «.proto»— and backed up, which is
    /// much faster than trying at every byte of the file.
    /// </summary>
    private static IEnumerable<int> Starts(byte[] data)
    {
        byte[] needle = ".proto"u8.ToArray();

        for (int i = 0; i + needle.Length <= data.Length; i++)
        {
            if (data[i] != needle[0]) continue;
            if (!data.AsSpan(i, needle.Length).SequenceEqual(needle)) continue;

            // The name can be of any length, so backing up is tried until a
            // 0x0A <length> header that fits exactly with what there is up to the «.proto».
            int end = i + needle.Length;
            for (int nameLength = needle.Length; nameLength <= 120; nameLength++)
            {
                int header = end - nameLength - 2;      // 0x0A + un byte de longitud
                if (header < 0) break;
                if (data[header] == 0x0A && data[header + 1] == nameLength) yield return header;
            }
        }
    }

    /// <summary>
    /// Reads a complete descriptor from there, if there is one.
    ///
    /// The length is not given: it walks field by field while what is read makes sense
    /// for a FileDescriptorProto, and each time the piece read up to that point survives the
    /// round trip it is noted as the best known end. The longest that fits is returned.
    /// </summary>
    private static Blob? ReadAt(byte[] data, int start)
    {
        FileDescriptorProto? best = null;
        int bestLength = 0;
        int at = start;

        while (at < data.Length)
        {
            if (!TryField(data, ref at)) break;

            int length = at - start;
            if (length < 24) continue;

            var file = TryParse(data.AsSpan(start, length).ToArray());
            if (file == null) continue;

            best = file;
            bestLength = length;
        }

        return best == null ? null : new Blob(start, bestLength, best);
    }

    /// <summary>A top-level field: advances the cursor if it is plausible.</summary>
    private static bool TryField(byte[] data, ref int at)
    {
        if (!TryVarint(data, ref at, out ulong key)) return false;

        int field = (int)(key >> 3);
        int wire = (int)(key & 7);

        // A FileDescriptorProto goes up to field 12. Beyond that we have already left the block.
        if (field is < 1 or > 12) return false;

        switch (wire)
        {
            case 0: return TryVarint(data, ref at, out _);
            case 2:
                if (!TryVarint(data, ref at, out ulong len)) return false;
                if (len > int.MaxValue || at + (int)len > data.Length) return false;
                at += (int)len;
                return true;
            default: return false;    // a descriptor uses neither fixed 32 nor 64 bits
        }
    }

    private static bool TryVarint(byte[] data, ref int at, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (at < data.Length && shift <= 63)
        {
            byte b = data[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    /// <summary>Decodes and checks. Returns null at the slightest doubt.</summary>
    public static FileDescriptorProto? TryParse(byte[] raw)
    {
        try
        {
            var file = FileDescriptorProto.Parser.ParseFrom(raw);
            if (!file.ToByteArray().AsSpan().SequenceEqual(raw)) return null;
            if (string.IsNullOrEmpty(file.Name)) return null;
            if (file.MessageType.Count == 0 && file.EnumType.Count == 0) return null;
            return file;
        }
        catch (InvalidProtocolBufferException) { return null; }
    }

    /// <summary>Joins what was found into a single FileDescriptorSet, which is what is stored.</summary>
    public static FileDescriptorSet AsSet(IEnumerable<Blob> blobs)
    {
        var set = new FileDescriptorSet();
        foreach (var blob in blobs) set.File.Add(blob.File);
        return set;
    }
}
