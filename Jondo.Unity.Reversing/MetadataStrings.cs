using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// The text strings the client carries inside.
///
/// A Unity client compiled with IL2CPP keeps all its literals in a single file,
/// global-metadata.dat, one after the other and in UTF-8. Nothing has to be deobfuscated to read them:
/// they are in the clear, and among them is the only thing that interests us here, the protocol's descriptor
/// that protobuf needs to work at run time.
///
/// The code protobuf generates for C# stores that descriptor like this:
///
///     byte[] descriptorData = Convert.FromBase64String(string.Concat("Cg...", "...", "..."));
///
/// That is: base64, and split into pieces when it is long. Hence the two things this does —
/// finding the pieces and returning them IN ORDER— because joining them back is the business of whoever
/// decodes them.
/// </summary>
public static class MetadataStrings
{
    /// <summary>A literal found inside the file, with where it was.</summary>
    public readonly record struct Found(int Offset, string Text);

    /// <summary>
    /// Extracts the strings that could be base64, of at least <paramref name="minimum"/> characters.
    ///
    /// Where it starts is not constrained on purpose. A descriptor's first piece starts with
    /// "Cg" —field 1 of a FileDescriptorProto, which is its name— but the second and the third
    /// start wherever the cut fell, so filtering by the start would leave out
    /// precisely the continuations.
    /// </summary>
    public static List<Found> Base64Like(string path, int minimum = 32)
    {
        byte[] data = File.ReadAllBytes(path);
        var found = new List<Found>();
        var run = new StringBuilder();
        int start = 0;

        for (int i = 0; i <= data.Length; i++)
        {
            byte b = i < data.Length ? data[i] : (byte)0;
            bool part = i < data.Length && IsBase64Char(b);

            if (part)
            {
                if (run.Length == 0) start = i;
                run.Append((char)b);
                continue;
            }

            if (run.Length >= minimum) found.Add(new Found(start, run.ToString()));
            run.Clear();
        }

        return found;
    }

    private static bool IsBase64Char(byte b)
        => (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') ||
           b == '+' || b == '/' || b == '=';
}
