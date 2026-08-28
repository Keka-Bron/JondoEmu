using System;
using System.Buffers.Binary;
using System.IO;

namespace Jondo.Unity.World.Client
{
    /// <summary>The languages the client ships a text table for.</summary>
    public enum GameLanguage
    {
        Spanish = 0,
        English = 1,
        French = 2,
        German = 3,
        Portuguese = 4,
    }

    /// <summary>
    /// The client's own text table: every name and every line of dialogue in the game, in one
    /// language.
    /// </summary>
    /// <remarks>
    /// Read straight out of <c>Content/I18n/{lang}.bin</c> rather than copied anywhere. The client
    /// is already on the disk, it already has all five languages, and a copy of ours would be one
    /// more thing to regenerate on patch day — and one more thing to be quietly out of date in the
    /// meantime.
    ///
    /// The format, worked out and then checked against <c>world.db</c>: 500 keys sampled at random
    /// came back byte for byte identical, including a 42,180-character one.
    ///
    /// <code>
    ///   byte  0      version, 2
    ///   bytes 1-2    a two-letter language tag
    ///   bytes 3-6    how many entries, int32 little-endian: 339,342
    ///   then         that many pairs of (int32 key, int32 offset), sorted by key
    ///   at offset    a 7-bit varint length, then that many bytes of UTF-8
    /// </code>
    ///
    /// The index being sorted is what makes this cheap: a key is found by bisecting 339,342 pairs,
    /// so nothing is built up front and nothing is held but the file itself.
    ///
    /// <see cref="Missing"/> comes back for a key that is not there, and that is a normal thing
    /// rather than a fault: <c>world.db</c> holds 339,175 of the 339,342, so a handful of keys
    /// exist on one side and not the other.
    /// </remarks>
    public sealed class ClientText
    {
        /// <summary>What a key that is not in the table gives back.</summary>
        public const string Missing = "";

        private const int HeaderBytes = 7;
        private const int EntryBytes = 8;

        private readonly byte[] _file;
        private readonly int _count;

        private ClientText(byte[] file, int count, GameLanguage language, string path)
        {
            _file = file;
            _count = count;
            Language = language;
            Path = path;
        }

        public GameLanguage Language { get; }

        /// <summary>Which file this came out of, for the overview screen.</summary>
        public string Path { get; }

        /// <summary>How many texts the table holds.</summary>
        public int Count => _count;

        /// <summary>The two-letter tag the client uses in the file name.</summary>
        public static string TagOf(GameLanguage language) => language switch
        {
            GameLanguage.English => "en",
            GameLanguage.French => "fr",
            GameLanguage.German => "de",
            GameLanguage.Portuguese => "pt",
            _ => "es",
        };

        /// <summary>
        /// Opens one language's table. Returns null and says why rather than throwing: the editor
        /// has to open on a machine with no client next to it, showing ids instead of names.
        /// </summary>
        public static ClientText? Open(string path, GameLanguage language, Action<string>? report = null)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                report?.Invoke($"{System.IO.Path.GetFileName(path ?? "(no path)")} is not there; " +
                               "names will show as numbers.");
                return null;
            }

            try
            {
                byte[] file = File.ReadAllBytes(path);
                if (file.Length < HeaderBytes)
                {
                    report?.Invoke($"{System.IO.Path.GetFileName(path)} is too short to be a text table.");
                    return null;
                }

                int count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(3, 4));
                if (count <= 0 || HeaderBytes + (long)count * EntryBytes > file.Length)
                {
                    report?.Invoke($"{System.IO.Path.GetFileName(path)} says it holds {count} texts, " +
                                   "which does not fit in the file.");
                    return null;
                }

                return new ClientText(file, count, language, path);
            }
            catch (Exception ex)
            {
                report?.Invoke($"{System.IO.Path.GetFileName(path)} could not be read: {ex.Message}");
                return null;
            }
        }

        /// <summary>The text for a key, or <see cref="Missing"/>.</summary>
        public string this[long key] => Of(key);

        public string Of(long key)
        {
            if (key <= 0 || key > int.MaxValue) return Missing;

            int offset = OffsetOf((int)key);
            return offset < 0 ? Missing : Read(offset);
        }

        /// <summary>True when the table has something for this key.</summary>
        public bool Has(long key) => key > 0 && key <= int.MaxValue && OffsetOf((int)key) >= 0;

        /// <summary>Bisects the index, which the client writes sorted by key.</summary>
        private int OffsetOf(int key)
        {
            int low = 0;
            int high = _count - 1;

            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                int at = HeaderBytes + middle * EntryBytes;
                int here = BinaryPrimitives.ReadInt32LittleEndian(_file.AsSpan(at, 4));

                if (here == key) return BinaryPrimitives.ReadInt32LittleEndian(_file.AsSpan(at + 4, 4));
                if (here < key) low = middle + 1;
                else high = middle - 1;
            }

            return -1;
        }

        /// <summary>A varint length and then that many bytes of UTF-8.</summary>
        private string Read(int offset)
        {
            int length = 0;
            int shift = 0;

            while (offset < _file.Length)
            {
                byte b = _file[offset++];
                length |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;

                shift += 7;

                // A length needs at most five bytes, and the longest text in the table is 42,180
                // characters. More than that means these are not the bytes we think they are.
                if (shift > 28) return Missing;
            }

            if (length < 0 || offset + length > _file.Length) return Missing;
            return System.Text.Encoding.UTF8.GetString(_file, offset, length);
        }
    }
}
