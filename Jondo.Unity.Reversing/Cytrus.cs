using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// Ankama's client store, from which an old client is downloaded without installing it.
///
/// The matcher measures 68.3% with zero errors when the protocol does not change, and 11.3% on the real
/// jump from 3.6.4.3 to 3.6.10.10. The difference is not the matcher: it is the six patches in
/// between. Each one moves the names a little and six chained moves erase the signal.
///
/// The cure is not to make the long jump. Ankama still serves the old clients on its CDN, so
/// the jump can be split into one-patch jumps —3.6.4.3, 3.6.5.4, 3.6.6.5, …— each one
/// close to the ceiling, dragging the names along the chain. This is what downloads them.
///
/// ─── Why the whole client is not downloaded ─────────────────────────────────────────────
///
/// A client is some 12 GB and of all that two files are needed, some 130 MB. The manifest
/// says which chunks each file is split into and which bundle each chunk lives in, and the bundles
/// accept range requests. So the exact bytes are asked for and nothing more: per version
/// those 130 MB are downloaded instead of the 12 GB, which is the difference between making the chain and not making it.
///
/// ─── The format ─────────────────────────────────────────────────────────────────────────
///
/// The manifest is a FlatBuffer without a file identifier. The schema is public
/// (dofusdude/ankabuffer) and fits in five tables, so the reader goes here by hand instead of
/// dragging in Google's package and its code generator to read five tables.
/// </summary>
public sealed class Cytrus : IDisposable
{
    private const string Cdn = "https://cytrus.cdn.ankama.com";

    /// <summary>dofera/cytrus's archive, which keeps ALL the published versions.</summary>
    ///
    /// Ankama's live cytrus.json only brings today's —3.5 KB— because it overwrites it on each
    /// release. dofera's merges it every minute instead of overwriting it, and that is why it keeps
    /// the two hundred Windows versions since 3.0.1.1. Without that list one does not know what to ask
    /// the CDN for: the files are still there, but one has to know what they are called.
    private const string Archive = "https://raw.githubusercontent.com/dofera/cytrus/main/cytrus.json";

    private readonly HttpClient _http;
    private readonly string _cache;
    private readonly string _game;
    private readonly string _platform;
    private readonly string _release;

    public Cytrus(string cache, string game = "dofus", string platform = "windows", string release = "dofus3")
    {
        _cache = cache;
        _game = game;
        _platform = platform;
        _release = release;
        Directory.CreateDirectory(cache);

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Jondo/1.0");
    }

    public void Dispose() => _http.Dispose();

    // ─── The versions ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// This branch's published versions, from the oldest to the newest.
    ///
    /// They come with the prefix on («6.0_3.6.10.10»), which is how the CDN names them. The order is
    /// the archive's, which is the order in which Ankama published them, and that is exactly the one needed
    /// for chaining: the chain has to walk the patches in the order they came out.
    /// </summary>
    public async Task<List<string>> VersionsAsync(CancellationToken cancel = default)
    {
        string file = Path.Combine(_cache, "cytrus-archivo.json");
        string json;
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(6))
        {
            json = await File.ReadAllTextAsync(file, cancel);
        }
        else
        {
            json = await _http.GetStringAsync(Archive, cancel);
            await File.WriteAllTextAsync(file, json, cancel);
        }

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var branch = doc.RootElement
            .GetProperty("games").GetProperty(_game)
            .GetProperty("platforms").GetProperty(_platform)
            .GetProperty(_release);

        var versions = new List<string>();
        foreach (var v in branch.EnumerateArray())
        {
            string name = v.GetString() ?? "";
            if (name.Length > 0) versions.Add(name);
        }
        return versions;
    }

    /// <summary>
    /// The stretch of chain that goes from one version to another, both included.
    ///
    /// It is given the ends as one writes them —«3.6.4.3»— and it returns the names the
    /// CDN understands. The versions the CDN no longer serves are left out here and not later:
    /// a chain missing a link halfway is not half a chain, it is two chains.
    ///
    /// The order is computed, not inherited. In the archive the versions are in the order they were
    /// seen —and the ones someone filled in by hand later go at the end, out of place—, so
    /// here they are sorted by number. Sorting them as text would be even worse: it would put 3.6.10.10
    /// before 3.6.9.9, and a chain walked backwards gives no warning, it simply matches
    /// badly and gives a bad percentage that looks like the matcher's.
    /// </summary>
    public async Task<List<string>> ChainAsync(string from, string to, Action<string> report, CancellationToken cancel = default)
    {
        var all = await VersionsAsync(cancel);
        all.Sort((a, b) => Compare(Tail(a), Tail(b)));

        int start = all.FindIndex(v => Tail(v) == from);
        int end = all.FindIndex(v => Tail(v) == to);
        if (start < 0) throw new InvalidOperationException("la versión " + from + " no está en el archivo de cytrus");
        if (end < 0) throw new InvalidOperationException("la versión " + to + " no está en el archivo de cytrus");
        if (end < start) (start, end) = (end, start);

        var chain = new List<string>();
        for (int i = start; i <= end; i++)
        {
            string version = all[i];
            if (await ServedAsync(version, cancel)) chain.Add(version);
            else report("  " + Tail(version) + ": la CDN ya no la sirve, se salta");
        }
        return chain;
    }

    /// <summary>Removes the branch prefix: «6.0_3.6.10.10» becomes «3.6.10.10».</summary>
    public static string Tail(string version)
    {
        int bar = version.IndexOf('_');
        return bar < 0 ? version : version[(bar + 1)..];
    }

    /// <summary>Compares two versions by their numbers, segment by segment.</summary>
    public static int Compare(string a, string b)
    {
        string[] left = a.Split('.'), right = b.Split('.');
        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            long x = i < left.Length && long.TryParse(left[i], out long l) ? l : -1;
            long y = i < right.Length && long.TryParse(right[i], out long r) ? r : -1;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private async Task<bool> ServedAsync(string version, CancellationToken cancel)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, ManifestUrl(version));
        using var answer = await _http.SendAsync(head, cancel);
        return answer.IsSuccessStatusCode;
    }

    private string ManifestUrl(string version)
        => Cdn + "/" + _game + "/releases/" + _release + "/" + _platform + "/" + version + ".manifest";

    // ─── The manifest ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A version's manifest, cached on disk.
    ///
    /// It is 51 MB per version and it is read more than once, so it is kept. It is written first to a
    /// temporary file and moved at the end: a half-downloaded manifest left with the
    /// good name would make all the following runs fail without saying why.
    /// </summary>
    public async Task<byte[]> ManifestAsync(string version, Action<string> report, CancellationToken cancel = default)
    {
        string file = Path.Combine(_cache, version + ".manifest");
        if (File.Exists(file)) return await File.ReadAllBytesAsync(file, cancel);

        report("  bajando el manifiesto de " + Tail(version) + "…");
        byte[] bytes = await _http.GetByteArrayAsync(ManifestUrl(version), cancel);

        string half = file + ".parcial";
        await File.WriteAllBytesAsync(half, bytes, cancel);
        File.Move(half, file, overwrite: true);
        return bytes;
    }

    // ─── The download ───────────────────────────────────────────────────────────────────

    /// <summary>A file that has been downloaded and has passed verification.</summary>
    public sealed record Grabbed(string Name, string Path, long Size);

    /// <summary>A chunk: its hash, where it starts inside the bundle and how much it takes up.</summary>
    private sealed record Piece(string Hash, long Offset, long Size);

    /// <summary>
    /// Downloads from a version only the files matching one of the patterns.
    ///
    /// The patterns are the good old shell ones («*GameAssembly.dll»,
    /// «*global-metadata.dat») and they are compared against the whole path inside the client.
    ///
    /// What is written is verified: the SHA-1 hash of the assembled file has to be the one the
    /// manifest says or nothing is written. There is no room for tolerance here —a GameAssembly.dll with one
    /// bad chunk would give a code index with invented evidence, and that already cost us nineteen
    /// false anchors the last time we let a piece of evidence through unchecked.
    /// </summary>
    public async Task<List<Grabbed>> FetchAsync(
        string version,
        string[] patterns,
        string destination,
        Action<string> report,
        CancellationToken cancel = default)
    {
        byte[] manifest = await ManifestAsync(version, report, cancel);
        Directory.CreateDirectory(destination);

        var got = new List<Grabbed>();
        var root = Flat.Root(manifest);

        for (int f = 0; f < root.Count(0); f++)
        {
            var fragment = root.Item(0, f);

            // Which files of this fragment interest us, and which chunks they ask for.
            var wanted = new List<(string Name, long Size, string Hash, List<string> Chunks)>();
            var needed = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < fragment.Count(1); i++)
            {
                var file = fragment.Item(1, i);
                string name = file.Text(0);
                long size = file.Long(1);
                if (size == 0 || !Matches(name, patterns)) continue;

                string hash = file.Hex(2);
                var pieces = new List<string>();
                int count = file.Count(3);
                if (count == 0)
                {
                    // No chunks: the whole file is one chunk, and its hash is the file's.
                    pieces.Add(hash);
                }
                else
                {
                    for (int c = 0; c < count; c++) pieces.Add(file.Item(3, c).Hex(0));
                }

                foreach (string piece in pieces) needed.Add(piece);
                wanted.Add((name, size, hash, pieces));
            }

            if (wanted.Count == 0) continue;

            // Where each chunk we need lives. A chunk can be in several bundles;
            // we keep the first one that appears, which is as good as any other.
            var lodging = new Dictionary<string, (string Bundle, Piece Piece)>(StringComparer.Ordinal);
            for (int b = 0; b < fragment.Count(2); b++)
            {
                var bundle = fragment.Item(2, b);
                string bundleHash = bundle.Hex(0);
                for (int c = 0; c < bundle.Count(1); c++)
                {
                    var chunk = bundle.Item(1, c);
                    string hash = chunk.Hex(0);
                    if (!needed.Contains(hash) || lodging.ContainsKey(hash)) continue;
                    lodging[hash] = (bundleHash, new Piece(hash, chunk.Long(2), chunk.Long(1)));
                }
            }

            string missing = needed.FirstOrDefault(h => !lodging.ContainsKey(h));
            if (missing is not null)
                throw new InvalidOperationException(
                    "el trozo " + missing + " no está en ningún paquete de «" + fragment.Text(0) + "»");

            var meat = await PullAsync(lodging, needed, cancel);

            foreach (var (name, size, hash, pieces) in wanted)
            {
                // The path inside the client is kept as is. It is not cosmetic: the reader
                // looks for global-metadata.dat in Dofus_Data\il2cpp_data\Metadata\ and not in the root,
                // so flattening the names would give a folder that cannot be opened.
                string path = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                Assemble(path, pieces, meat, size, hash, name);
                got.Add(new Grabbed(name, path, size));
                report("  " + name + "  " + Human(size) + "  verificado");
            }
        }

        return got;
    }

    /// <summary>
    /// Fetches the chunks, grouping by bundle and joining the ones that go in a row.
    ///
    /// The chunks of one same file usually go consecutively inside the bundle, so joining them
    /// turns hundreds of requests into a few. A single range is asked for per request on
    /// purpose: asking for several at once forces the response to come in several parts, with their
    /// separators and their headers, and that is one more parser that can get it wrong.
    /// </summary>
    private async Task<Dictionary<string, byte[]>> PullAsync(
        Dictionary<string, (string Bundle, Piece Piece)> lodging,
        HashSet<string> needed,
        CancellationToken cancel)
    {
        var meat = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var group in needed.Select(h => lodging[h]).GroupBy(x => x.Bundle))
        {
            var pieces = group.Select(x => x.Piece).OrderBy(p => p.Offset).ToList();

            int at = 0;
            while (at < pieces.Count)
            {
                int last = at;
                while (last + 1 < pieces.Count &&
                       pieces[last + 1].Offset == pieces[last].Offset + pieces[last].Size) last++;

                long from = pieces[at].Offset;
                long to = pieces[last].Offset + pieces[last].Size - 1;
                byte[] run = await RangeAsync(group.Key, from, to, cancel);

                for (int i = at; i <= last; i++)
                {
                    var piece = pieces[i];
                    var slice = new byte[piece.Size];
                    Array.Copy(run, (int)(piece.Offset - from), slice, 0, (int)piece.Size);
                    meat[piece.Hash] = slice;
                }

                at = last + 1;
            }
        }

        return meat;
    }

    /// <summary>A byte range of a bundle, with three attempts because the network is the network.</summary>
    private async Task<byte[]> RangeAsync(string bundle, long from, long to, CancellationToken cancel)
    {
        string url = Cdn + "/" + _game + "/bundles/" + bundle[..2] + "/" + bundle;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var ask = new HttpRequestMessage(HttpMethod.Get, url);
                ask.Headers.Range = new RangeHeaderValue(from, to);
                using var answer = await _http.SendAsync(ask, cancel);
                answer.EnsureSuccessStatusCode();

                byte[] bytes = await answer.Content.ReadAsByteArrayAsync(cancel);
                long expected = to - from + 1;
                if (bytes.LongLength != expected)
                    throw new InvalidOperationException(
                        "pedí " + expected + " bytes y llegaron " + bytes.LongLength);
                return bytes;
            }
            catch (Exception) when (attempt < 3 && !cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancel);
            }
        }
    }

    /// <summary>Joins the chunks in order and checks the hash before leaving the file in place.</summary>
    private static void Assemble(
        string path, List<string> pieces, Dictionary<string, byte[]> meat, long size, string hash, string name)
    {
        string half = path + ".parcial";
        string got;
        long written;

        using (var output = File.Create(half))
        using (var sha = SHA1.Create())
        {
            foreach (string piece in pieces)
            {
                byte[] bytes = meat[piece];
                output.Write(bytes);
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0);
            got = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            written = output.Length;
        }

        if (got != hash)
        {
            File.Delete(half);
            throw new InvalidOperationException(
                name + ": la huella no cuadra (esperaba " + hash + ", salió " + got + ")");
        }
        if (written != size)
        {
            File.Delete(half);
            throw new InvalidOperationException(
                name + ": esperaba " + size + " bytes y salieron " + written);
        }

        File.Move(half, path, overwrite: true);
    }

    private static bool Matches(string name, string[] patterns)
    {
        string flat = name.Replace('\\', '/');
        foreach (string pattern in patterns)
        {
            if (Glob(flat, pattern.Replace('\\', '/'))) return true;
        }
        return false;
    }

    /// <summary>The usual wildcard: «*» stands for anything, including nothing.</summary>
    private static bool Glob(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                t++; p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++; mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1; t = ++mark;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    public static string Human(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0") + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0") + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.0") + " KB",
        _ => bytes + " B",
    };

    // ─── The FlatBuffers reader ─────────────────────────────────────────────────────────

    /// <summary>
    /// Just enough to read the manifest's five tables.
    ///
    /// A table carries in front an integer with what has to be subtracted to reach its vtable, and the
    /// vtable says at which offset each field lives, or zero if it is not there. The field indices
    /// are the schema's, in declaration order:
    ///
    ///   Chunk    { 0 hash[], 1 size, 2 offset, 3 done }
    ///   File     { 0 name, 1 size, 2 hash[], 3 chunks[], 4 executable, 5 symlink }
    ///   Bundle   { 0 hash[], 1 chunks[] }
    ///   Fragment { 0 name, 1 files[], 2 bundles[] }
    ///   Manifest { 0 fragments[] }
    /// </summary>
    private readonly struct Flat(byte[] data, int at)
    {
        private readonly byte[] _data = data;
        private readonly int _at = at;

        public static Flat Root(byte[] data) => new(data, BinaryPrimitives.ReadInt32LittleEndian(data));

        /// <summary>Where the field lives, or zero if the vtable says it is not there.</summary>
        private int Where(int index)
        {
            int vtable = _at - BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_at));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(vtable));
            int slot = 4 + index * 2;
            if (slot >= length) return 0;
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(vtable + slot));
            return offset == 0 ? 0 : _at + offset;
        }

        /// <summary>Strings, vectors and tables are stored by relative reference.</summary>
        private int Follow(int position)
            => position + BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(position));

        public long Long(int index)
        {
            int position = Where(index);
            return position == 0 ? 0 : BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(position));
        }

        public string Text(int index)
        {
            int position = Where(index);
            if (position == 0) return "";
            int vector = Follow(position);
            int length = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(vector));
            return Encoding.UTF8.GetString(_data, vector + 4, length);
        }

        /// <summary>A byte vector read as hexadecimal, which is how hashes are named.</summary>
        public string Hex(int index)
        {
            int position = Where(index);
            if (position == 0) return "";
            int vector = Follow(position);
            int length = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(vector));
            return Convert.ToHexString(_data, vector + 4, length).ToLowerInvariant();
        }

        public int Count(int index)
        {
            int position = Where(index);
            if (position == 0) return 0;
            return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Follow(position)));
        }

        /// <summary>Element i of a vector of tables.</summary>
        public Flat Item(int index, int i)
        {
            int vector = Follow(Where(index));
            int slot = vector + 4 + i * 4;
            return new Flat(_data, slot + BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(slot)));
        }
    }
}
