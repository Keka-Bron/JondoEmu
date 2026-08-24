using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Los vendedores que Jondo junta en uno solo.
    ///
    /// El catálogo de tiendas está medido del servidor de torneos de Ankama, y allí cada categoría
    /// va partida por tramos de nivel: «Sombreros 1 - 49», «Sombreros 50 - 99», «Sombreros 100 -
    /// 149»... cinco vendedores para lo mismo, en fila y todos en el mapa del zaap de Amakna. Ocho
    /// categorías están así, y suman 38 NPCs que aquí se convierten en 9.
    ///
    /// Lo que se junta y cómo se llama sale de datos/vendedores_jondo.json, que lee también el mod
    /// del cliente: el nombre y el catálogo salen del mismo sitio y no se pueden descuadrar.
    ///
    /// EL LÍMITE que manda en todo esto: el mensaje que lleva el catálogo —el kbd— no está
    /// paginado, y no hay ni un caso en las capturas de dos kbd para una misma tienda, así que no
    /// hay prueba de que el cliente sepa juntarlos. El mayor que Ankama manda son 444 entradas y
    /// 26.902 bytes. Siete de las ocho categorías caben de sobra; las armas juntas serían 683
    /// objetos, un 54 % por encima de nada medido, y por eso van en dos vendedores de 355 y 333.
    /// </summary>
    public static class Vendors
    {
        /// <summary>Un vendedor que se queda, con lo que se le echa encima.</summary>
        public sealed class Merge
        {
            /// <summary>El que sobrevive: conserva su casilla y su tienda.</summary>
            public int Keeps;

            /// <summary>Cómo se llamará en la pantalla del jugador.</summary>
            public string Name = "";

            /// <summary>Su clave de texto, la que sustituye el mod del cliente.</summary>
            public int NameId;

            /// <summary>Los que desaparecen.</summary>
            public List<int> Absorbs = new List<int>();
        }

        private static readonly List<Merge> _merges = new List<Merge>();
        private static readonly HashSet<int> _absorbed = new HashSet<int>();

        /// <summary>Los que ya no se siembran porque otro se ha quedado con su catálogo.</summary>
        public static IReadOnlyCollection<int> Absorbed => _absorbed;

        public static IReadOnlyList<Merge> All => _merges;

        public static int Count => _merges.Count;

        public static void Initialize()
        {
            _merges.Clear();
            _absorbed.Clear();

            string path = Paths.JondoVendorsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine("[Vendedores] No se junta ninguno: no hay " +
                                  $"{Path.GetFileName(path)}.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("vendedores", out var vendedores))
                {
                    Console.WriteLine("[Vendedores] El fichero no tiene «vendedores».");
                    return;
                }

                foreach (var entrada in vendedores.EnumerateObject())
                {
                    if (!int.TryParse(entrada.Name, out int keeps)) continue;

                    var merge = new Merge { Keeps = keeps };
                    if (entrada.Value.TryGetProperty("nombre", out var nombre))
                        merge.Name = nombre.GetString() ?? "";
                    if (entrada.Value.TryGetProperty("nameId", out var nameId))
                        merge.NameId = nameId.GetInt32();

                    if (entrada.Value.TryGetProperty("absorbe", out var absorbe))
                    {
                        foreach (var otro in absorbe.EnumerateArray())
                        {
                            int id = otro.GetInt32();

                            // Un vendedor no puede absorberse a sí mismo ni ser absorbido dos
                            // veces: lo primero borraría su propia tienda al quitarlo del mapa, y
                            // lo segundo dejaría su catálogo repetido en dos sitios.
                            if (id == keeps)
                            {
                                Console.WriteLine($"[Vendedores] El {keeps} se absorbe a sí mismo; " +
                                                  "se ignora esa línea.");
                                continue;
                            }
                            if (!_absorbed.Add(id))
                            {
                                Console.WriteLine($"[Vendedores] El {id} lo absorben dos vendedores; " +
                                                  "se queda con el primero.");
                                continue;
                            }
                            merge.Absorbs.Add(id);
                        }
                    }

                    _merges.Add(merge);
                }

                // Y ninguno de los que se quedan puede estar en la lista de los que desaparecen.
                foreach (var merge in _merges)
                {
                    if (!_absorbed.Contains(merge.Keeps)) continue;
                    Console.WriteLine($"[Vendedores] El {merge.Keeps} se queda Y desaparece a la " +
                                      "vez. Se queda.");
                    _absorbed.Remove(merge.Keeps);
                }

                Console.WriteLine($"[Vendedores] {_merges.Count} vendedor(es) se quedan con lo de " +
                                  $"otros {_absorbed.Count}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Vendedores] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>Si a ese vendedor lo ha absorbido otro y por tanto ya no se siembra.</summary>
        public static bool IsAbsorbed(int npcTemplateId) => _absorbed.Contains(npcTemplateId);
    }
}
