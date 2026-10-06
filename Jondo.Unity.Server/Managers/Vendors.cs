using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The sellers Jondo merges into one.
    ///
    /// The shop catalogue is measured from Ankama's tournament server, and there each category
    /// goes split by level brackets: «Sombreros 1 - 49», «Sombreros 50 - 99», «Sombreros 100 -
    /// 149»... five sellers for the same thing, in a row and all on the Amakna zaap map. Eight
    /// categories are like that, and they add up to 38 NPCs that here become 9.
    ///
    /// What is merged and what it is called comes from datos/vendedores_jondo.json, which the client
    /// mod also reads: the name and the catalogue come from the same place and cannot drift apart.
    ///
    /// THE LIMIT that rules all this: the message carrying the catalogue —the kbd— is not
    /// paginated, and there is not a single case in the captures of two kbd for one same shop, so there
    /// is no proof that the client knows how to join them. The biggest Ankama sends is 444 entries and
    /// 26,902 bytes. Seven of the eight categories fit easily; the weapons together would be 683
    /// items, 54 % above anything measured, and that is why they go in two sellers of 355 and 333.
    /// </summary>
    public static class Vendors
    {
        /// <summary>A seller that stays, with what is piled onto it.</summary>
        public sealed class Merge
        {
            /// <summary>The survivor: it keeps its cell and its shop.</summary>
            public int Keeps;

            /// <summary>What it will be called on the player's screen.</summary>
            public string Name = "";

            /// <summary>Its text key, the one the client mod replaces.</summary>
            public int NameId;

            /// <summary>The ones that disappear.</summary>
            public List<int> Absorbs = new List<int>();
        }

        /// <summary>Where a seller stands: its cell and which way it faces.</summary>
        public readonly struct Placement
        {
            public Placement(int cell, int orientation) { Cell = cell; Orientation = orientation; }
            public int Cell { get; }
            public int Orientation { get; }
        }

        private static readonly List<Merge> _merges = new List<Merge>();
        private static readonly HashSet<int> _absorbed = new HashSet<int>();
        private static readonly Dictionary<int, Placement> _placements =
            new Dictionary<int, Placement>();

        /// <summary>The ones no longer seeded because another has taken their catalogue.</summary>
        public static IReadOnlyCollection<int> Absorbed => _absorbed;

        public static IReadOnlyList<Merge> All => _merges;

        public static int Count => _merges.Count;

        public static void Initialize()
        {
            _merges.Clear();
            _absorbed.Clear();
            _placements.Clear();

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

                            // A seller cannot absorb itself nor be absorbed twice:
                            // the first would delete its own shop when removing it from the map, and
                            // the second would leave its catalogue repeated in two places.
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

                // And none of the ones that stay can be on the list of the ones that disappear.
                foreach (var merge in _merges)
                {
                    if (!_absorbed.Contains(merge.Keeps)) continue;
                    Console.WriteLine($"[Vendedores] El {merge.Keeps} se queda Y desaparece a la " +
                                      "vez. Se queda.");
                    _absorbed.Remove(merge.Keeps);
                }

                // And where each one stands.
                //
                // It goes here and not in the NpcSpawns table on purpose: bases/ is not versioned, so
                // a placement written in world.db is lost as soon as someone
                // unzips world.zip again. This does travel with the repository.
                if (doc.RootElement.TryGetProperty("colocacion", out var colocacion))
                {
                    foreach (var entrada in colocacion.EnumerateObject())
                    {
                        if (!int.TryParse(entrada.Name, out int npcId)) continue;
                        int casilla = entrada.Value.TryGetProperty("casilla", out var c) ? c.GetInt32() : 0;
                        int orientacion = entrada.Value.TryGetProperty("orientacion", out var o)
                            ? o.GetInt32() : 1;
                        if (casilla > 0) _placements[npcId] = new Placement(casilla, orientacion);
                    }
                }

                Console.WriteLine($"[Vendedores] {_merges.Count} vendedor(es) se quedan con lo de " +
                                  $"otros {_absorbed.Count}; {_placements.Count} colocado(s) a mano.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Vendedores] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>Whether that seller has been absorbed by another and is therefore no longer seeded.</summary>
        public static bool IsAbsorbed(int npcTemplateId) => _absorbed.Contains(npcTemplateId);

        /// <summary>Where that seller goes, if Jondo places it by hand.</summary>
        public static Placement? PlacementOf(int npcTemplateId)
            => _placements.TryGetValue(npcTemplateId, out var sitio) ? sitio : null;
    }
}
