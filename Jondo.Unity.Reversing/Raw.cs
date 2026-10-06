using System.Buffers.Binary;
using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// global-metadata.dat read raw, without going through LibCpp2IL.
///
/// One gets here after three attempts at asking the library —fields, properties,
/// <c>GetDefaultValue</c>— and after all three returned zero over 71,190 entries. When the
/// answer is zero in ALL, the bug is not in the data: it is in how it is asked.
///
/// Here there is nothing to guess. The offsets are given by the header, and the sizes confirm the
/// structure without any room for doubt:
///
///   fieldDefaultValues       631,476 / 12 = 52,623  ← exactly the entries the header says
///   parameterDefaultValues   222,804 / 12 = 18,567  ← ditto
///
/// Twelve bytes per entry and three integers inside. With that the table can be walked by hand.
/// </summary>
public static class Raw
{
    /// <summary>An entry of the default value tables.</summary>
    /// <param name="Owner">The field or parameter the value belongs to.</param>
    /// <param name="Data">Where the value is, relative to the data area.</param>
    public readonly record struct Entry(int Owner, int TypeIndex, int Data);

    /// <summary>Reads a default value table: three integers per entry.</summary>
    public static List<Entry> Defaults(byte[] file, long offset, long size)
    {
        var entries = new List<Entry>((int)(size / 12));
        for (long at = offset; at + 12 <= offset + size; at += 12)
        {
            entries.Add(new Entry(
                BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)at)),
                BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)at + 4)),
                BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)at + 8))));
        }
        return entries;
    }

    /// <summary>
    /// The value stored at that position, if it is a string.
    ///
    /// IL2CPP stores strings with the length in front as a compressed integer: if the first byte
    /// is below 0x80 the length is that byte and that is it, and if not it takes two or four. The protocol's
    /// names measure between 40 and 130 characters, so almost all fall in the two-byte
    /// case; all three are covered so as not to leave the odd case out.
    /// </summary>
    public static string? Text(byte[] file, long at)
    {
        if (at < 0 || at >= file.Length) return null;

        // The length is an int32, not a compressed integer.
        //
        // The first version read it as compressed and the strings came out with garbage in front:
        // «\0\0\0</col» instead of «</color>». It is what happens when taking the first byte
        // of a small int32 as the length and starting to read three bytes too early. It was seen because I forced the
        // probe to show any strings instead of only the ones I was looking for; with the filter on
        // the bug would have passed as «there is nothing here».
        if (at + 4 > file.Length) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)at));
        at += 4;

        if (length <= 0 || length > 4096 || at + length > file.Length) return null;
        return Encoding.UTF8.GetString(file, (int)at, length);
    }

    /// <summary>A string from the names table, which are zero-terminated.</summary>
    public static string Name(byte[] file, long offset, int index)
    {
        long at = offset + index;
        if (at < 0 || at >= file.Length) return "";

        long end = at;
        while (end < file.Length && file[end] != 0) end++;
        return Encoding.UTF8.GetString(file, (int)at, (int)(end - at));
    }

    /// <summary>A field's name index, from the fields table (three integers per entry).</summary>
    public static int FieldNameIndex(byte[] file, long fieldsOffset, int fieldIndex)
    {
        long at = fieldsOffset + (long)fieldIndex * 12;
        if (at < 0 || at + 4 > file.Length) return -1;
        return BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)at));
    }
}
