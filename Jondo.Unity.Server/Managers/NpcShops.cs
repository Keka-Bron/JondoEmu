using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Qué vende cada NPC.
    ///
    /// No está inventado: sale de los cincuenta y seis catálogos que el servidor de torneos mandó
    /// en la captura, emparejando cada `kbd` con el `iov` que lo pidió y sacando la plantilla del
    /// NPC del `jss` de ese mapa. Son cincuenta y un vendedores y 5.508 objetos distintos, y el
    /// reparto es por tipo de objeto y tramo de nivel: "Armas 1 - 49", "Capas 200", "Escudos"...
    ///
    /// El precio también es el medido. El servidor de torneos pone casi todo a un kama, las
    /// monturas a cero y deja cuatro excepciones con su precio de catálogo. Lo que NO vale es el
    /// precio de la plantilla del objeto: de los 5.508, sólo 1.508 coinciden con él.
    ///
    /// Lo genera tools/extraer_tiendas.py.
    /// </summary>
    public static class NpcShops
    {
        /// <summary>Lo que cuesta un objeto si el catálogo medido no dice otra cosa.</summary>
        public const long DefaultPrice = 1;

        private static readonly Dictionary<int, int[]> _byNpc = new();
        private static readonly Dictionary<int, long> _prices = new();
        private static readonly Dictionary<int, string> _effects = new();

        public static int Count => _byNpc.Count;

        public static int Items { get; private set; }

        public static void Initialize()
        {
            _byNpc.Clear();
            _prices.Clear();
            Items = 0;

            string path = Paths.NpcShopsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Tiendas] Falta {Path.GetFileName(path)}; los NPCs no venderán nada. " +
                                  "Genéralo con tools/extraer_tiendas.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                if (root.TryGetProperty("npcs", out var npcs))
                {
                    foreach (var entry in npcs.EnumerateObject())
                    {
                        if (!int.TryParse(entry.Name, out int npcId)) continue;

                        var gids = new List<int>();
                        foreach (var gid in entry.Value.EnumerateArray())
                        {
                            if (gid.ValueKind == JsonValueKind.Number) gids.Add(gid.GetInt32());
                        }
                        _byNpc[npcId] = gids.ToArray();
                    }
                }

                if (root.TryGetProperty("precios", out var prices))
                {
                    foreach (var entry in prices.EnumerateObject())
                    {
                        if (int.TryParse(entry.Name, out int gid) && entry.Value.ValueKind == JsonValueKind.Number)
                        {
                            _prices[gid] = entry.Value.GetInt64();
                        }
                    }
                }

                JuntarVendedores();

                var distinct = new HashSet<int>();
                foreach (var gids in _byNpc.Values) distinct.UnionWith(gids);
                Items = distinct.Count;

                LoadEffects(distinct);

                Console.WriteLine($"[Tiendas] {_byNpc.Count} vendedores, {Items} objetos distintos, " +
                                  $"{_effects.Count} con efectos.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tiendas] No se ha podido leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Junta en uno solo los vendedores que Ankama parte por tramos de nivel.
        ///
        /// El catálogo del que se queda pasa a ser el suyo más el de todos los que absorbe, sin
        /// repetidos y conservando el orden: primero lo suyo y después lo de los otros en el orden
        /// en que estén escritos. El orden importa porque es el que ve el jugador en la lista.
        ///
        /// Y se avisa si algún catálogo pasa de 444 entradas, que es el mayor kbd que manda el
        /// servidor real —26.902 bytes— y por tanto lo único que sabemos que el cliente digiere.
        /// El mensaje no está paginado y no hay ni un caso en las capturas de dos kbd para una
        /// misma tienda, así que por encima de esa cifra estamos en terreno sin medir.
        /// </summary>
        private static void JuntarVendedores()
        {
            if (Managers.Vendors.Count == 0) return;

            foreach (var merge in Managers.Vendors.All)
            {
                var juntos = new List<int>();
                var vistos = new HashSet<int>();

                if (_byNpc.TryGetValue(merge.Keeps, out var suyos))
                    foreach (int gid in suyos) if (vistos.Add(gid)) juntos.Add(gid);

                foreach (int otro in merge.Absorbs)
                {
                    if (!_byNpc.TryGetValue(otro, out var deOtro)) continue;
                    foreach (int gid in deOtro) if (vistos.Add(gid)) juntos.Add(gid);
                    _byNpc.Remove(otro);
                }

                if (juntos.Count == 0) continue;
                _byNpc[merge.Keeps] = juntos.ToArray();

                string aviso = juntos.Count > CatalogoMedidoMayor
                    ? $"  <-- POR ENCIMA DE LAS {CatalogoMedidoMayor} MEDIDAS"
                    : "";
                Console.WriteLine($"[Vendedores] «{merge.Name}» ({merge.Keeps}): {juntos.Count} " +
                                  $"objetos de {merge.Absorbs.Count + 1} vendedor(es).{aviso}");
            }
        }

        /// <summary>
        /// El catálogo más grande que manda el servidor real, en entradas: 444, y 26.902 bytes.
        /// Por encima de ahí no hay medida que lo respalde.
        /// </summary>
        private const int CatalogoMedidoMayor = 444;

        /// <summary>
        /// Los efectos de cada objeto que se vende, en la forma que entiende
        /// <see cref="Equipment.ParseEffects"/>.
        ///
        /// Se cargan todos de una vez y no objeto a objeto: un catálogo son hasta 444 entradas y
        /// cada una tiene sus efectos, así que ir a la base por cada una serían miles de consultas
        /// cada vez que alguien abre una tienda. Aquí son dos lecturas enteras y luego memoria.
        ///
        /// El valor de estreno es el tope del dado, igual que hace la creación de personaje con el
        /// conjunto del aventurero: un objeto recién comprado sale con lo mejor de su horquilla.
        /// </summary>
        private static void LoadEffects(HashSet<int> gids)
        {
            _effects.Clear();

            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();

            // Rid -> "[efecto,valor,0,0]", de una sola pasada por ItemEffects.
            var byRid = new Dictionary<long, string>();
            var effects = connection.CreateCommand();
            effects.CommandText = "SELECT Rid, EffectId, DiceNum, DiceSide, Value FROM ItemEffects;";
            using (var reader = effects.ExecuteReader())
            {
                while (reader.Read())
                {
                    int id = reader.GetInt32(1);
                    if (id == 0) continue;

                    int diceNum = reader.GetInt32(2);
                    int diceSide = reader.GetInt32(3);
                    int value = reader.GetInt32(4);
                    int fixed_ = value != 0 ? value : (diceSide != 0 ? diceSide : diceNum);

                    byRid[reader.GetInt64(0)] = $"[{id},{fixed_},0,0]";
                }
            }

            var templates = connection.CreateCommand();
            templates.CommandText = "SELECT Id, Data FROM ItemTemplates;";
            using var rows = templates.ExecuteReader();
            while (rows.Read())
            {
                int gid = rows.GetInt32(0);
                if (!gids.Contains(gid) || rows.IsDBNull(1)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(rows.GetString(1));
                    if (!doc.RootElement.TryGetProperty("possibleEffects", out var possible)) continue;
                    if (!possible.TryGetProperty("Array", out var list)) continue;
                    if (list.ValueKind != JsonValueKind.Array) continue;

                    var pieces = new List<string>();
                    foreach (var entry in list.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("rid", out var rid)) continue;
                        if (byRid.TryGetValue(rid.GetInt64(), out string? piece)) pieces.Add(piece);
                    }

                    if (pieces.Count > 0) _effects[gid] = "[" + string.Join(",", pieces) + "]";
                }
                catch { }
            }
        }

        /// <summary>Los efectos de fábrica de un objeto que se vende, o "[]" si no tiene.</summary>
        public static string EffectsOf(int gid)
            => _effects.TryGetValue(gid, out string? json) ? json : "[]";

        /// <summary>Lo que tiene a la venta ese NPC, en el orden en que lo mandaba el servidor real.</summary>
        public static IReadOnlyList<int> CatalogueOf(int npcId)
            => _byNpc.TryGetValue(npcId, out var gids) ? gids : (IReadOnlyList<int>)Array.Empty<int>();

        public static bool Sells(int npcId) => _byNpc.ContainsKey(npcId);

        /// <summary>
        /// Lo que cuesta. El fichero sólo apunta los 317 precios que NO son un kama —313 de ellos
        /// a cero, que son las monturas, los arreos y un sombrero— porque los otros 5.191 valían
        /// todos lo mismo y no hacía falta repetirlo.
        /// </summary>
        public static long PriceOf(int gid)
            => _prices.TryGetValue(gid, out long price) ? price : DefaultPrice;
    }
}
