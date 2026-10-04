using System.Buffers.Binary;
using System.Text;

namespace Jondo.Unity.Cytrus;

/// <summary>A piece of a file or of a bundle: its hash, its length and where it starts.</summary>
/// <remarks>
/// The same chunk is listed twice in a manifest. Under a file, <see cref="Offset"/> is where the
/// chunk goes inside that file; under a bundle, it is where the chunk sits inside the bundle.
/// Its hash is the SHA-1 of its bytes either way, which is what makes each one checkable alone.
/// </remarks>
public readonly record struct CytrusChunk(Sha1Hash Hash, long Size, long Offset);

/// <summary>A file of the client, as the manifest describes it.</summary>
/// <param name="Name">Path inside the client folder, with forward slashes.</param>
/// <param name="Chunks">As listed; may be empty for small files, see <see cref="Pieces"/>.</param>
public sealed record CytrusFile(string Name, long Size, Sha1Hash Hash, CytrusChunk[] Chunks, bool Executable)
{
    /// <summary>
    /// What the file is made of: its chunks, or the whole file as a single chunk when the
    /// manifest lists none. That happens with small files, and then the file's own hash is the
    /// chunk's hash.
    /// </summary>
    public IReadOnlyList<CytrusChunk> Pieces
        => Chunks.Length > 0 || Size == 0 ? Chunks : new[] { new CytrusChunk(Hash, Size, 0) };
}

/// <summary>One downloadable blob on the CDN: a run of chunks, all from the same fragment.</summary>
public sealed record CytrusBundle(Sha1Hash Hash, CytrusChunk[] Chunks);

/// <summary>A named part of a client that is installed as a whole, such as <c>map_textures_4x</c>.</summary>
public sealed record CytrusFragment(string Name, CytrusFile[] Files, CytrusBundle[] Bundles)
{
    /// <summary>The installed size: the sum of the files, not of the bundles.</summary>
    public long Size => Files.Sum(f => f.Size);
}

/// <summary>
/// A Cytrus manifest: the list of fragments of one client version.
/// </summary>
/// <remarks>
/// The manifest is a FlatBuffer with no file identifier. Its schema is public
/// (dofusdude/ankabuffer) and fits in five tables, so it is read by hand here instead of pulling
/// in Google's package and code generator for five tables. Field indexes follow the schema's
/// declaration order:
///
///   Chunk    { 0 hash[], 1 size, 2 offset, 3 done }
///   File     { 0 name, 1 size, 2 hash[], 3 chunks[], 4 executable, 5 symlink }
///   Bundle   { 0 hash[], 1 chunks[] }
///   Fragment { 0 name, 1 files[], 2 bundles[] }
///   Manifest { 0 fragments[] }
///
/// A whole manifest is about 50 MB and describes 12 GB of client, so fragments are only
/// materialized when asked for: the launcher wants two of the thirteen. Nothing here reads or
/// holds file contents.
///
/// A truncated or corrupt manifest throws <see cref="InvalidDataException"/>, never an index
/// error: the caller has to be able to tell "the download went wrong" from a bug.
/// </remarks>
public sealed class CytrusManifest
{
    private readonly byte[] _data;
    private readonly string[] _names;

    private CytrusManifest(byte[] data, string[] names)
    {
        _data = data;
        _names = names;
    }

    /// <summary>The fragment names, in manifest order.</summary>
    public IReadOnlyList<string> FragmentNames => _names;

    public static CytrusManifest Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Guard(() =>
        {
            var root = Flat.Root(data);
            var names = new string[root.Count(0)];
            for (int i = 0; i < names.Length; i++) names[i] = root.Item(0, i).Text(0);
            return new CytrusManifest(data, names);
        });
    }

    /// <summary>One fragment, fully read, or null if the manifest has none by that name.</summary>
    public CytrusFragment? Fragment(string name)
    {
        int index = Array.IndexOf(_names, name);
        return index < 0 ? null : Guard(() => Read(Flat.Root(_data).Item(0, index)));
    }

    /// <summary>Every fragment, read one at a time as the enumeration reaches it.</summary>
    public IEnumerable<CytrusFragment> Fragments()
    {
        for (int i = 0; i < _names.Length; i++)
        {
            int index = i;
            yield return Guard(() => Read(Flat.Root(_data).Item(0, index)));
        }
    }

    private static CytrusFragment Read(Flat fragment)
    {
        var files = new CytrusFile[fragment.Count(1)];
        for (int i = 0; i < files.Length; i++)
        {
            var file = fragment.Item(1, i);
            files[i] = new CytrusFile(
                file.Text(0),
                file.Long(1),
                file.Hash(2),
                Chunks(file, 3),
                file.Byte(4) != 0);
        }

        var bundles = new CytrusBundle[fragment.Count(2)];
        for (int b = 0; b < bundles.Length; b++)
        {
            var bundle = fragment.Item(2, b);
            bundles[b] = new CytrusBundle(bundle.Hash(0), Chunks(bundle, 1));
        }

        return new CytrusFragment(fragment.Text(0), files, bundles);
    }

    private static CytrusChunk[] Chunks(Flat table, int field)
    {
        var chunks = new CytrusChunk[table.Count(field)];
        for (int c = 0; c < chunks.Length; c++)
        {
            var chunk = table.Item(field, c);
            chunks[c] = new CytrusChunk(chunk.Hash(0), chunk.Long(1), chunk.Long(2));
            if (chunks[c].Size < 0 || chunks[c].Offset < 0)
                throw new InvalidDataException("a chunk with a negative size or offset");
        }
        return chunks;
    }

    private static T Guard<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ArgumentException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("the Cytrus manifest is truncated or corrupt", ex);
        }
    }

    // ─── The FlatBuffers reader ─────────────────────────────────────────────────────────

    /// <summary>
    /// Just enough of FlatBuffers to walk the five tables.
    /// </summary>
    /// <remarks>
    /// A table starts with the signed distance back to its vtable, and the vtable says at which
    /// offset each field lives, or zero if it is absent. Strings, vectors and sub-tables are
    /// stored as relative references. Every read goes through a span, so an offset that points
    /// outside the buffer throws instead of reading garbage.
    /// </remarks>
    private readonly struct Flat(byte[] data, int at)
    {
        private readonly byte[] _data = data;
        private readonly int _at = at;

        public static Flat Root(byte[] data) => new(data, BinaryPrimitives.ReadInt32LittleEndian(data));

        /// <summary>Where the field lives, or zero if the vtable says it is absent.</summary>
        private int Where(int index)
        {
            int vtable = _at - BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_at));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(vtable));
            int slot = 4 + index * 2;
            if (slot >= length) return 0;
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(vtable + slot));
            return offset == 0 ? 0 : _at + offset;
        }

        private int Follow(int position)
            => position + BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(position));

        public long Long(int index)
        {
            int position = Where(index);
            return position == 0 ? 0 : BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(position));
        }

        public byte Byte(int index)
        {
            int position = Where(index);
            return position == 0 ? (byte)0 : _data[position];
        }

        public string Text(int index)
        {
            var bytes = Vector(index);
            return bytes.IsEmpty ? "" : Encoding.UTF8.GetString(bytes);
        }

        /// <summary>A byte vector read as a SHA-1; empty when absent.</summary>
        public Sha1Hash Hash(int index)
        {
            var bytes = Vector(index);
            if (bytes.IsEmpty) return default;
            if (bytes.Length != Sha1Hash.Length)
                throw new InvalidDataException("a hash of " + bytes.Length + " bytes, expected " + Sha1Hash.Length);
            return Sha1Hash.FromBytes(bytes);
        }

        private ReadOnlySpan<byte> Vector(int index)
        {
            int position = Where(index);
            if (position == 0) return default;
            int vector = Follow(position);
            int length = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(vector));
            return _data.AsSpan(vector + 4, length);
        }

        public int Count(int index)
        {
            int position = Where(index);
            if (position == 0) return 0;
            int count = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Follow(position)));
            if (count < 0) throw new InvalidDataException("a vector with a negative length");
            return count;
        }

        /// <summary>Element <paramref name="i"/> of a vector of tables.</summary>
        public Flat Item(int index, int i)
        {
            int vector = Follow(Where(index));
            int slot = vector + 4 + i * 4;
            return new Flat(_data, slot + BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(slot)));
        }
    }
}
