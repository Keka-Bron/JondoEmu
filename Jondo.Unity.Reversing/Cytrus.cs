using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// El almacén de clientes de Ankama, del que se baja un cliente viejo sin instalarlo.
///
/// El emparejador mide 68,3% con cero errores cuando el protocolo no cambia, y 11,3% en el salto
/// real de 3.6.4.3 a 3.6.10.10. La diferencia no es el emparejador: son los seis parches que hay en
/// medio. Cada uno mueve un poco los nombres y seis movimientos encadenados borran la señal.
///
/// La cura es no dar el salto largo. Ankama sigue sirviendo los clientes antiguos en su CDN, así
/// que el salto se puede partir en saltos de un parche —3.6.4.3, 3.6.5.4, 3.6.6.5, …— cada uno
/// cerca del techo, arrastrando los nombres por la cadena. Esto es lo que los baja.
///
/// ─── Por qué no se baja el cliente entero ───────────────────────────────────────────────
///
/// Un cliente son unos 12 GB y de todo eso hacen falta dos ficheros, unos 130 MB. El manifiesto
/// dice en qué trozos está partido cada fichero y en qué paquete vive cada trozo, y los paquetes
/// admiten peticiones por rango. Así que se piden los bytes exactos y nada más: por versión se
/// bajan esos 130 MB en vez de los 12 GB, que es la diferencia entre hacer la cadena y no hacerla.
///
/// ─── El formato ─────────────────────────────────────────────────────────────────────────
///
/// El manifiesto es un FlatBuffer sin identificador de fichero. El esquema es público
/// (dofusdude/ankabuffer) y cabe en cinco tablas, así que el lector va aquí a mano en vez de
/// arrastrar el paquete de Google y su generador de código para leer cinco tablas.
/// </summary>
public sealed class Cytrus : IDisposable
{
    private const string Cdn = "https://cytrus.cdn.ankama.com";

    /// <summary>El archivo de dofera/cytrus, que conserva TODAS las versiones publicadas.</summary>
    ///
    /// El cytrus.json vivo de Ankama sólo trae las de hoy —3,5 KB— porque lo sobrescribe en cada
    /// publicación. El de dofera lo fusiona cada minuto en vez de sobrescribirlo, y por eso guarda
    /// las doscientas versiones de Windows desde la 3.0.1.1. Sin esa lista no se sabe qué pedirle
    /// a la CDN: los ficheros siguen ahí, pero hay que saber cómo se llaman.
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

    // ─── Las versiones ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Las versiones publicadas de esta rama, de la más vieja a la más nueva.
    ///
    /// Vienen con el prefijo puesto («6.0_3.6.10.10»), que es como las nombra la CDN. El orden es
    /// el del archivo, que es el orden en que Ankama las publicó, y ése es justo el que hace falta
    /// para encadenar: la cadena tiene que recorrer los parches en el orden en que salieron.
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
    /// El trozo de cadena que va de una versión a otra, ambas incluidas.
    ///
    /// Se le dan los extremos tal y como los escribe uno —«3.6.4.3»— y devuelve los nombres que
    /// entiende la CDN. Las versiones que la CDN ya no sirve se quedan fuera aquí y no más tarde:
    /// una cadena a la que le falta un eslabón a mitad no es media cadena, es dos cadenas.
    ///
    /// El orden se calcula, no se hereda. En el archivo las versiones están en el orden en que se
    /// vieron —y las que alguien rellenó a mano después van al final, fuera de sitio—, así que
    /// aquí se ordenan por número. Ordenarlas como texto sería peor todavía: pondría 3.6.10.10
    /// antes que 3.6.9.9, y una cadena recorrida al revés no avisa de nada, simplemente empareja
    /// mal y da un porcentaje malo que parece del emparejador.
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

    /// <summary>Quita el prefijo de rama: «6.0_3.6.10.10» pasa a ser «3.6.10.10».</summary>
    public static string Tail(string version)
    {
        int bar = version.IndexOf('_');
        return bar < 0 ? version : version[(bar + 1)..];
    }

    /// <summary>Compara dos versiones por sus números, tramo a tramo.</summary>
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

    // ─── El manifiesto ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// El manifiesto de una versión, cacheado en disco.
    ///
    /// Son 51 MB por versión y se lee más de una vez, así que se guarda. Se escribe primero a un
    /// fichero temporal y se mueve al final: un manifiesto a medio bajar que se quedara con el
    /// nombre bueno haría fallar todas las ejecuciones siguientes sin decir por qué.
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

    // ─── La bajada ──────────────────────────────────────────────────────────────────────

    /// <summary>Un fichero que se ha bajado y ha pasado la verificación.</summary>
    public sealed record Grabbed(string Name, string Path, long Size);

    /// <summary>Un trozo: su huella, dónde empieza dentro del paquete y cuánto ocupa.</summary>
    private sealed record Piece(string Hash, long Offset, long Size);

    /// <summary>
    /// Baja de una versión sólo los ficheros que casan con alguno de los patrones.
    ///
    /// Los patrones son los del intérprete de órdenes de toda la vida («*GameAssembly.dll»,
    /// «*global-metadata.dat») y se comparan contra la ruta entera dentro del cliente.
    ///
    /// Lo que se escribe se verifica: la huella SHA-1 del fichero armado tiene que ser la que dice
    /// el manifiesto o no se escribe nada. Aquí no cabe la tolerancia —un GameAssembly.dll con un
    /// trozo mal daría un índice de código con pruebas inventadas, y eso ya nos costó diecinueve
    /// anclas falsas la última vez que dejamos pasar una evidencia sin comprobar.
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

            // Qué ficheros de este fragmento nos interesan, y qué trozos piden.
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
                    // Sin trozos: el fichero entero es un trozo, y su huella es la del fichero.
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

            // Dónde vive cada trozo que nos hace falta. Un trozo puede estar en varios paquetes;
            // nos quedamos con el primero que aparezca, que es tan bueno como cualquier otro.
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
                // La ruta de dentro del cliente se respeta tal cual. No es cosmético: el lector
                // busca global-metadata.dat en Dofus_Data\il2cpp_data\Metadata\ y no en la raíz,
                // así que aplanar los nombres daría una carpeta que no se puede abrir.
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
    /// Se trae los trozos, agrupando por paquete y juntando los que van seguidos.
    ///
    /// Los trozos de un mismo fichero suelen ir consecutivos dentro del paquete, así que juntarlos
    /// convierte cientos de peticiones en unas pocas. Se pide un solo rango por petición a
    /// propósito: pedir varios de golpe obliga a la respuesta a venir en varias partes, con sus
    /// separadores y sus cabeceras, y eso es un analizador más que puede equivocarse.
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

    /// <summary>Un rango de bytes de un paquete, con tres intentos porque la red es la red.</summary>
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

    /// <summary>Junta los trozos en orden y comprueba la huella antes de dejar el fichero puesto.</summary>
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

    /// <summary>El comodín de siempre: «*» vale por cualquier cosa, incluida ninguna.</summary>
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

    // ─── El lector de FlatBuffers ───────────────────────────────────────────────────────

    /// <summary>
    /// Lo justo para leer las cinco tablas del manifiesto.
    ///
    /// Una tabla lleva delante un entero con lo que hay que restar para llegar a su vtable, y la
    /// vtable dice en qué desplazamiento vive cada campo, o cero si no está. Los índices de campo
    /// son los del esquema, por orden de declaración:
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

        /// <summary>Dónde vive el campo, o cero si la vtable dice que no está.</summary>
        private int Where(int index)
        {
            int vtable = _at - BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_at));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(vtable));
            int slot = 4 + index * 2;
            if (slot >= length) return 0;
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(vtable + slot));
            return offset == 0 ? 0 : _at + offset;
        }

        /// <summary>Las cadenas, los vectores y las tablas se guardan por referencia relativa.</summary>
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

        /// <summary>Un vector de bytes leído como hexadecimal, que es como se nombran las huellas.</summary>
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

        /// <summary>El elemento i de un vector de tablas.</summary>
        public Flat Item(int index, int i)
        {
            int vector = Follow(Where(index));
            int slot = vector + 4 + i * 4;
            return new Flat(_data, slot + BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(slot)));
        }
    }
}
