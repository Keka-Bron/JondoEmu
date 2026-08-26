using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Las tiendas que cobran en un OBJETO en vez de en kamas.
    ///
    /// Esto no es un invento de Jondo: el cliente 3.6.10.10 ya lo sabe hacer, y está medido. De los
    /// 60 mensajes de apertura de tienda —el kbd— que hay en las 305 capturas, 58 llevan sólo los
    /// campos f1 y f2 y cobran en kamas; los otros dos llevan un f3 con el id del objeto que hace
    /// de moneda: el 13052 «Sebuscalón» en la tienda de la Torre de los Viajeros y el 30529
    /// «Fidelicha» en una de Pandala. Con ese campo puesto, el cliente pinta la ficha en lugar del
    /// símbolo de las kamas y pide confirmación con el número de fichas.
    ///
    /// Y la compra cambia sólo en dos mensajes, los dos medidos en la captura de la Torre:
    ///
    ///   lqn 364   en vez del 252. Seis parámetros: el objeto comprado y su uid, la cantidad, el
    ///             precio, y el id y el uid de la moneda. Medido: 798, 1055401001, 1, 20, 13052, 0.
    ///   ivj       en vez del ivf. Lleva { f2: el uid de la pila de fichas, f3: LO QUE QUEDA }.
    ///             Que el f3 es el total nuevo y no lo gastado se ve en el mercadillo de runas de
    ///             otra captura, donde la misma pila va 107 -> 117 -> 217 -> 1217.
    ///
    /// Lo que vende cada tienda de fichas y a qué precio NO sale de ninguna captura: es contenido
    /// nuestro. Por eso vive en su propio fichero, datos/tiendas_en_fichas.json, escrito a mano y
    /// no generado. El catálogo normal, datos/npc_shops.json, lo rehace tools/extraer_tiendas.py
    /// cada vez que se vuelve a medir, así que meter esto ahí sería perderlo en la siguiente vuelta.
    ///
    /// Si el fichero no está, o está vacío, no pasa nada: ninguna tienda cobra en fichas y todo
    /// sigue exactamente igual que antes.
    /// </summary>
    public static class TokenShops
    {
        /// <summary>Una tienda que cobra en fichas: qué moneda pide y a cuánto vende cada cosa.</summary>
        public sealed class Shop
        {
            /// <summary>La plantilla del objeto que hace de moneda.</summary>
            public int TokenGid;

            /// <summary>Precio en fichas de un objeto concreto, por plantilla. Manda sobre todo.</summary>
            public Dictionary<int, long> Prices = new Dictionary<int, long>();

            /// <summary>
            /// Precio por TIPO de objeto, que es lo que hace esto manejable.
            ///
            /// Los vendedores de apariencia llevan 1.848 prendas entre los cinco. Escribir un
            /// precio por prenda serían 1.848 líneas que nadie va a revisar y que se descuadran en
            /// cuanto se añada una capa. Por tipo son nueve números: los sombreros valen lo mismo
            /// entre ellos y una montura vale más que un sombrero, que es la única distinción que
            /// de verdad importa.
            /// </summary>
            public Dictionary<int, long> PricesByType = new Dictionary<int, long>();

            /// <summary>Lo que vale en esta tienda lo que no encaje en ninguna de las dos tablas.</summary>
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

                    // Sin moneda no es una tienda de fichas. Se salta en vez de cobrar en kamas
                    // por accidente, que es lo que haría el cliente con un f3 a cero.
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

        /// <summary>La tienda de fichas de ese vendedor, o null si cobra en kamas como todos.</summary>
        public static Shop? Of(int npcTemplateId)
            => _byNpc.TryGetValue(npcTemplateId, out var shop) ? shop : null;

        /// <summary>
        /// Cuántas fichas cuesta un objeto en esa tienda.
        ///
        /// Se mira en tres sitios y gana el más concreto: el precio de ESE objeto, después el de su
        /// tipo, después el de la tienda entera. Lo que no esté en ninguno vale
        /// <see cref="DefaultPrice"/> y no cero: regalar cosas por olvidarse de una línea del
        /// fichero es peor que cobrarlas baratas.
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
        /// El tipo de cada objeto que vende alguna tienda de fichas, para poder cobrar por tipo.
        ///
        /// Sólo los de esas tiendas, no las 21.748 plantillas del juego: se lee una vez al arrancar
        /// y no se vuelve a la base en cada compra.
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

        /// <summary>Lo que cuesta un objeto al que no se le ha puesto precio.</summary>
        public const long DefaultPrice = 1;
    }
}
