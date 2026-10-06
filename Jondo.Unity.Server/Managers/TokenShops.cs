using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The shops that charge in an ITEM instead of in kamas.
    ///
    /// This is not a Jondo invention: the 3.6.10.10 client already knows how to do it, and it is measured. Of the
    /// 60 shop opening messages —the kbd— in the 305 captures, 58 carry only the
    /// fields f1 and f2 and charge in kamas; the other two carry an f3 with the id of the item acting
    /// as currency: 13052 «Sebuscalón» in the Travellers' Tower shop and 30529
    /// «Fidelicha» in one in Pandala. With that field set, the client draws the token instead of the
    /// kama symbol and asks for confirmation with the number of tokens.
    ///
    /// And the purchase changes in only two messages, both measured in the Tower capture:
    ///
    ///   lqn 364   instead of 252. Six parameters: the item bought and its uid, the quantity, the
    ///             price, and the currency's id and uid. Measured: 798, 1055401001, 1, 20, 13052, 0.
    ///   ivj       instead of ivf. It carries { f2: the uid of the token stack, f3: WHAT IS LEFT }.
    ///             That f3 is the new total and not what was spent is seen in the rune marketplace of
    ///             another capture, where the same stack goes 107 -> 117 -> 217 -> 1217.
    ///
    /// What each token shop sells and at what price does NOT come from any capture: it is our
    /// content. That is why it lives in its own file, datos/tiendas_en_fichas.json, written by hand and
    /// not generated. The normal catalogue, datos/npc_shops.json, is remade by tools/extraer_tiendas.py
    /// every time it is measured again, so putting this there would be losing it on the next round.
    ///
    /// If the file is not there, or is empty, nothing happens: no shop charges in tokens and everything
    /// stays exactly as before.
    /// </summary>
    public static class TokenShops
    {
        /// <summary>A shop that charges in tokens: which currency it asks for and how much it sells each thing for.</summary>
        public sealed class Shop
        {
            /// <summary>The template of the item acting as currency.</summary>
            public int TokenGid;

            /// <summary>Price in tokens of a specific item, by template. It rules over everything.</summary>
            public Dictionary<int, long> Prices = new Dictionary<int, long>();

            /// <summary>
            /// Price per item TYPE, which is what makes this manageable.
            ///
            /// The appearance sellers carry 1,848 garments among the five. Writing a
            /// price per garment would be 1,848 lines nobody is going to review and that go out of line as
            /// soon as a cape is added. Per type it is nine numbers: hats cost the same
            /// among themselves and a mount costs more than a hat, which is the only distinction that
            /// really matters.
            /// </summary>
            public Dictionary<int, long> PricesByType = new Dictionary<int, long>();

            /// <summary>What anything that fits in neither table is worth in this shop.</summary>
            public long ShopPrice;
        }

        private static readonly Dictionary<int, Shop> _byNpc = new Dictionary<int, Shop>();

        public static int Count => _byNpc.Count;

        public static void Initialize()
        {
            _byNpc.Clear();

            string path = Paths.TokenShopsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine("[Tiendas] Ninguna tienda cobra en fichas: no hay " +
                                  $"{Path.GetFileName(path)}.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("tiendas", out var tiendas))
                {
                    Console.WriteLine("[Tiendas] El fichero de tiendas en fichas no tiene «tiendas».");
                    return;
                }

                foreach (var entrada in tiendas.EnumerateObject())
                {
                    if (!int.TryParse(entrada.Name, out int npcId)) continue;

                    var shop = new Shop();
                    if (entrada.Value.TryGetProperty("moneda", out var moneda))
                        shop.TokenGid = moneda.GetInt32();

                    // Without a currency it is not a token shop. It is skipped instead of charging in kamas
                    // by accident, which is what the client would do with an f3 at zero.
                    if (shop.TokenGid <= 0)
                    {
                        Console.WriteLine($"[Tiendas] El vendedor {npcId} no dice qué moneda pide; " +
                                          "se ignora.");
                        continue;
                    }

                    if (entrada.Value.TryGetProperty("precio", out var suelto))
                        shop.ShopPrice = suelto.GetInt64();

                    if (entrada.Value.TryGetProperty("preciosPorTipo", out var porTipo))
                    {
                        foreach (var precio in porTipo.EnumerateObject())
                        {
                            if (!int.TryParse(precio.Name, out int tipo)) continue;
                            shop.PricesByType[tipo] = precio.Value.GetInt64();
                        }
                    }

                    if (entrada.Value.TryGetProperty("precios", out var precios))
                    {
                        foreach (var precio in precios.EnumerateObject())
                        {
                            if (!int.TryParse(precio.Name, out int gid)) continue;
                            shop.Prices[gid] = precio.Value.GetInt64();
                        }
                    }

                    _byNpc[npcId] = shop;
                }

                LoadItemTypes();

                int sueltos = 0, tipos = 0;
                foreach (var s in _byNpc.Values) { sueltos += s.Prices.Count; tipos += s.PricesByType.Count; }
                Console.WriteLine($"[Tiendas] {_byNpc.Count} tienda(s) que cobran en fichas: " +
                                  $"{tipos} precio(s) por tipo, {sueltos} por objeto, " +
                                  $"{_typeOfItem.Count} objeto(s) con su tipo cargado.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tiendas] No se pudo leer el fichero de tiendas en fichas: {ex.Message}");
            }
        }

        /// <summary>That seller's token shop, or null if it charges in kamas like everyone.</summary>
        public static Shop? Of(int npcTemplateId)
            => _byNpc.TryGetValue(npcTemplateId, out var shop) ? shop : null;

        /// <summary>
        /// How many tokens an item costs in that shop.
        ///
        /// It is looked up in three places and the most specific wins: the price of THAT item, then that of its
        /// type, then that of the whole shop. What is in none of them is worth
        /// <see cref="DefaultPrice"/> and not zero: giving things away for forgetting a line of the
        /// file is worse than charging little for them.
        /// </summary>
        public static long PriceOf(Shop shop, int gid)
        {
            if (shop == null) return DefaultPrice;
            if (shop.Prices.TryGetValue(gid, out long precio)) return precio;
            if (shop.PricesByType.Count > 0 &&
                _typeOfItem.TryGetValue(gid, out int tipo) &&
                shop.PricesByType.TryGetValue(tipo, out long porTipo)) return porTipo;
            return shop.ShopPrice > 0 ? shop.ShopPrice : DefaultPrice;
        }

        /// <summary>
        /// The type of each item some token shop sells, to be able to charge by type.
        ///
        /// Only those of these shops, not the game's 21,748 templates: it is read once at start
        /// and the base is not visited again on each purchase.
        /// </summary>
        private static readonly Dictionary<int, int> _typeOfItem = new Dictionary<int, int>();

        private static void LoadItemTypes()
        {
            _typeOfItem.Clear();

            var necesarios = new HashSet<int>();
            foreach (var shop in _byNpc)
            {
                if (shop.Value.PricesByType.Count == 0) continue;
                foreach (int gid in NpcShops.CatalogueOf(shop.Key)) necesarios.Add(gid);
            }
            if (necesarios.Count == 0) return;

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Type FROM ItemTemplates WHERE Id = $id;";
                var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                foreach (int gid in necesarios)
                {
                    id.Value = gid;
                    if (command.ExecuteScalar() is long tipo) _typeOfItem[gid] = (int)tipo;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tiendas] No se pudo leer el tipo de los objetos: {ex.Message}");
            }
        }

        /// <summary>What an item with no price set costs.</summary>
        public const long DefaultPrice = 1;
    }
}
