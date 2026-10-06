using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What each NPC sells.
    ///
    /// It is not invented: it comes from the fifty-six catalogues the tournament server sent
    /// in the capture, pairing each `kbd` with the `iov` that asked for it and taking the
    /// NPC's template from that map's `jss`. They are fifty-one sellers and 5,508 distinct items, and the
    /// split is by item type and level bracket: "Armas 1 - 49", "Capas 200", "Escudos"...
    ///
    /// The price is also the measured one. The tournament server puts almost everything at one kama, the
    /// mounts at zero and leaves four exceptions with their catalogue price. What does NOT hold is the
    /// item template's price: of the 5,508, only 1,508 match it.
    ///
    /// tools/extraer_tiendas.py generates it.
    /// </summary>
    public static class NpcShops
    {
        /// <summary>What an item costs if the measured catalogue does not say otherwise.</summary>
        public const long DefaultPrice = 1;

        /// <summary>The hand-written catalogues, relative to the content root.</summary>
        public const string AuthoredFile = "npcs/shops.json";

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
                ApplyAuthored(Paths.ContentFile(AuthoredFile));

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
        /// Merges into one the sellers Ankama splits by level brackets.
        ///
        /// The catalogue of the one kept becomes its own plus that of all it absorbs, with no
        /// duplicates and keeping the order: first its own and then the others' in the order
        /// they are written. The order matters because it is the one the player sees in the list.
        ///
        /// And a warning is given if any catalogue goes over 444 entries, which is the biggest kbd the
        /// real server sends —26,902 bytes— and therefore the only thing we know the client digests.
        /// The message is not paginated and there is not a single case in the captures of two kbd for one
        /// same shop, so above that figure we are on unmeasured ground.
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
        /// The biggest catalogue the real server sends, in entries: 444, and 26,902 bytes.
        /// Above that there is no measurement to back it.
        /// </summary>
        private const int CatalogoMedidoMayor = 444;

        /// <summary>
        /// The effects of each item sold, in the shape
        /// <see cref="Equipment.ParseEffects"/> understands.
        ///
        /// They are all loaded at once and not item by item: a catalogue is up to 444 entries and
        /// each one has its effects, so going to the database for each one would be thousands of queries
        /// every time someone opens a shop. Here it is two whole reads and then memory.
        ///
        /// The brand-new value is the top of the die, just as character creation does with the
        /// adventurer's set: a freshly bought item comes out with the best of its range.
        /// </summary>
        private static void LoadEffects(HashSet<int> gids)
        {
            _effects.Clear();

            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();

            // Rid -> "[effect,value,0,0]", in a single pass over ItemEffects.
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

        /// <summary>
        /// The hand-written sellers, on top of the measured ones.
        /// </summary>
        /// <remarks>
        /// datos/npc_shops.json is remade by a tool, so a seller added there is
        /// lost on the next pass without a word; ours lives in content/npcs/shops.json
        /// and is laid ON TOP at start. A written seller replaces its whole catalogue -it is
        /// what has been decided it sells- and a written price is that item's wherever it is sold.
        /// </remarks>
        internal static void ApplyAuthored(string path)
        {
            if (!File.Exists(path)) return;

            int vendors = 0;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("shops", out var shops)) return;

                foreach (var shop in shops.EnumerateArray())
                {
                    if (!shop.TryGetProperty("npc", out var npc) || !shop.TryGetProperty("items", out var items)) continue;

                    var gids = new List<int>();
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("gid", out var gid)) continue;
                        int id = gid.GetInt32();
                        if (id <= 0 || gids.Contains(id)) continue;
                        gids.Add(id);
                        if (item.TryGetProperty("price", out var price)) _prices[id] = price.GetInt64();
                    }

                    if (gids.Count == 0) continue;
                    _byNpc[npc.GetInt32()] = gids.ToArray();
                    vendors++;
                }

                if (vendors > 0) Console.WriteLine($"[Tiendas] {vendors} vendedor(es) escritos a mano, de {AuthoredFile}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tiendas] No se ha podido leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>For the tests: an in-memory catalogue without going through the file.</summary>
        internal static void Forget()
        {
            _byNpc.Clear();
            _prices.Clear();
            _effects.Clear();
        }

        /// <summary>The factory effects of an item that is sold, or "[]" if it has none.</summary>
        public static string EffectsOf(int gid)
            => _effects.TryGetValue(gid, out string? json) ? json : "[]";

        /// <summary>What that NPC has for sale, in the order the real server sent it.</summary>
        public static IReadOnlyList<int> CatalogueOf(int npcId)
            => _byNpc.TryGetValue(npcId, out var gids) ? gids : (IReadOnlyList<int>)Array.Empty<int>();

        public static bool Sells(int npcId) => _byNpc.ContainsKey(npcId);

        /// <summary>
        /// What it costs. The file only records the 317 prices that are NOT one kama —313 of them
        /// at zero, which are the mounts, the harnesses and a hat— because the other 5,191 were
        /// all worth the same and there was no need to repeat it.
        /// </summary>
        public static long PriceOf(int gid)
            => _prices.TryGetValue(gid, out long price) ? price : DefaultPrice;
    }
}
