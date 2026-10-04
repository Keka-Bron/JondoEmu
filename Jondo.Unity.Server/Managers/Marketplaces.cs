using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The marketplaces of the world: the seven kinds, what each one takes and on what terms, and
    /// the counter that opens each one, city by city.
    /// </summary>
    /// <remarks>
    /// ─── One marketplace per kind, for the whole world ────────────────────────────────────
    ///
    /// The client's AuctionHousesDataRoot has seven rows, 1261 to 1267, one per kind, and the
    /// real server sends that id in the f4 of every kdw: 1262 opening the equipment counter of
    /// Bonta, 1264 the runes', 1265 the creatures', 1266 the souls', 1267 the cosmetics'. There is
    /// no id per city to tell Bonta's equipment from Astrub's, so the listings are SHARED: what is
    /// put on sale at one counter of a kind is on sale at every counter of that kind.
    ///
    /// ─── Where it comes from ──────────────────────────────────────────────────────────────
    ///
    /// datos/mercadillos_3.6.10.10.json, generated from the client's own data and the captures:
    /// the item types and settings are the kdw's where a capture opens that marketplace (five of
    /// seven) and inferred from the client's item categories for resources and consumables; the
    /// counters come from the "Mercadillos" hints of the client and their jss declarations. Its
    /// comment says which is which, row by row, and which hints could not be resolved.
    /// </remarks>
    public static class Marketplaces
    {
        /// <summary>One marketplace, as its kdw describes it.</summary>
        public sealed class House
        {
            /// <summary>The id of AuctionHousesDataRoot, and the kdw's f4.</summary>
            public int Id { get; init; }

            /// <summary>The interactive type its counters are declared with in the jss (f6).</summary>
            public int InteractiveType { get; init; }

            public string Name { get; init; } = "";

            /// <summary>The lot sizes on sale, in the order prices travel: 1, 10, 100, 1000.</summary>
            public IReadOnlyList<int> Lots { get; init; } = Array.Empty<int>();

            /// <summary>The item types it takes, in the kdw's order.</summary>
            public IReadOnlyList<int> ItemTypes { get; init; } = Array.Empty<int>();

            /// <summary>kdw f1: -1 in every capture, a counter being no NPC.</summary>
            public int NpcContextualId { get; init; }

            /// <summary>kdw f2: 200. INFERRED to be the highest item level taken.</summary>
            public int MaxItemLevel { get; init; }

            /// <summary>kdw f3: 708. INFERRED to be how many lots an account may have on sale here.</summary>
            public int MaxListings { get; init; }

            /// <summary>kdw f5: 2.0, the percentage of the price paid to put a lot on sale.</summary>
            public float TaxPercentage { get; init; }

            /// <summary>kdw f6: 672 hours, the 2,419,200 seconds a new listing's kes counts down.</summary>
            public int HoursOnSale { get; init; }

            /// <summary>kdw f7: 1.0, sent as it is.</summary>
            public float TaxModificationPercentage { get; init; }

            /// <summary>kdw f8: absent in every capture.</summary>
            public int Unknown8 { get; init; }

            /// <summary>Whether a capture opens it, or its types and settings are inferred.</summary>
            public bool Measured { get; init; }

            private HashSet<int>? _types;

            public bool Accepts(int itemType) => (_types ??= new HashSet<int>(ItemTypes)).Contains(itemType);

            /// <summary>The index of a lot size in the price lists, or -1 when it is not one.</summary>
            public int LotIndex(int quantity)
            {
                for (int i = 0; i < Lots.Count; i++) if (Lots[i] == quantity) return i;
                return -1;
            }

            /// <summary>
            /// The tax to put a lot on sale, as the client works it out to show it
            /// (AuctionHouseSell.UpdateTax): the price times the percentage over a hundred, in
            /// single precision, rounded to the nearest with halves to even, and never under 1 --
            /// nothing at all when the percentage is 0. 999 kamas cost 20 in the equipment capture,
            /// the one measurement, which this matches.
            /// </summary>
            public long Tax(long price) => TaxAt(price, TaxPercentage);

            /// <summary>
            /// The tax to change the price of a lot on sale, the same window's other branch: the
            /// whole tax on the new price when it goes up, the modification percentage (kdw f7,
            /// 1.0) on the new price when it stays or goes down.
            /// </summary>
            public long ModificationTax(long oldPrice, long newPrice)
                => TaxAt(newPrice, oldPrice < newPrice ? TaxPercentage : TaxModificationPercentage);

            private static long TaxAt(long price, float percentage)
            {
                if (percentage == 0f || price <= 0) return 0;
                float share = (float)price * percentage / 100f;
                return (long)Math.Max(Math.Round((double)share, MidpointRounding.ToEven), 1.0);
            }

            public TimeSpan OnSale => TimeSpan.FromHours(HoursOnSale);
        }

        /// <summary>A hint-resolved counter: which marketplace and how we know.</summary>
        public sealed class Counter
        {
            public int ElementId { get; init; }
            public int HouseId { get; init; }

            /// <summary>jss, iwo, pista+grafico or pista+grafico unico: see the data file.</summary>
            public string Source { get; init; } = "";
        }

        /// <summary>
        /// The seven marketplaces, their settings and item types, and the counter each one is.
        /// Generated from the client's AuctionHouses, Interactives and Hints and from the captures.
        /// </summary>
        public const string DataFile = "mercadillos_3.6.10.10.json";

        private static readonly Dictionary<int, House> _houses = new Dictionary<int, House>();
        private static readonly Dictionary<int, Counter> _counters = new Dictionary<int, Counter>();

        /// <summary>
        /// The skill every counter is declared and used with, "Consultar": the f2 of the jss
        /// declarations and the f4 of the iwn that answers the iwo, 355 in all of them.
        /// </summary>
        public static int Skill { get; private set; }

        public static IReadOnlyCollection<House> All => _houses.Values;
        public static int CounterCount => _counters.Count;

        public static void Initialize()
        {
            _houses.Clear();
            _counters.Clear();
            Skill = 0;

            string path = Paths.Resolve(DataFile);
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Marketplaces] {Path.GetFileName(path)} is missing; no marketplace " +
                                  "can be opened.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                Skill = root.GetProperty("habilidad").GetInt32();

                foreach (var entry in root.GetProperty("mercadillos").EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int id)) continue;
                    var v = entry.Value;
                    var settings = v.GetProperty("ajustes");
                    _houses[id] = new House
                    {
                        Id = id,
                        InteractiveType = v.GetProperty("tipoInteractivo").GetInt32(),
                        Name = v.GetProperty("nombre").GetString() ?? "",
                        Lots = Ints(v.GetProperty("lotes")),
                        ItemTypes = Ints(v.GetProperty("tiposDeObjeto")),
                        NpcContextualId = settings.GetProperty("f1").GetInt32(),
                        MaxItemLevel = settings.GetProperty("f2").GetInt32(),
                        MaxListings = settings.GetProperty("f3").GetInt32(),
                        TaxPercentage = settings.GetProperty("f5").GetSingle(),
                        HoursOnSale = settings.GetProperty("f6").GetInt32(),
                        TaxModificationPercentage = settings.GetProperty("f7").GetSingle(),
                        Unknown8 = settings.GetProperty("f8").GetInt32(),
                        Measured = v.GetProperty("ajustesFuente").GetString() == "kdw",
                    };
                }

                foreach (var entry in root.GetProperty("elementos").EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int element)) continue;
                    int house = entry.Value.GetProperty("mercadillo").GetInt32();
                    if (!_houses.ContainsKey(house)) continue;
                    _counters[element] = new Counter
                    {
                        ElementId = element,
                        HouseId = house,
                        Source = entry.Value.GetProperty("fuente").GetString() ?? "",
                    };
                }

                Console.WriteLine($"[Marketplaces] {_houses.Count} marketplaces, {_counters.Count} counters.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Marketplaces] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        private static IReadOnlyList<int> Ints(JsonElement array)
        {
            var list = new List<int>();
            foreach (var n in array.EnumerateArray()) list.Add(n.GetInt32());
            return list;
        }

        public static bool TryGet(int houseId, out House house) => _houses.TryGetValue(houseId, out house!);

        /// <summary>How many counters open a marketplace, all cities together.</summary>
        public static int CountersOf(int houseId) => _counters.Values.Count(c => c.HouseId == houseId);

        /// <summary>The marketplace a counter opens, by its element.</summary>
        public static bool TryGetByElement(int elementId, out House house)
        {
            house = null!;
            return _counters.TryGetValue(elementId, out var counter) && _houses.TryGetValue(counter.HouseId, out house!);
        }

        /// <summary>The counters standing on a map, with the element each one is.</summary>
        /// <remarks>
        /// By element and not by graphic: Brakmar's five counters share one graphic and each opens
        /// another marketplace. An element seen from two maps -- 522691 stands on the border of
        /// 212600837 and 212600325 -- is a counter on both, as the real jss declares it on both.
        /// </remarks>
        public static IEnumerable<(Interactives.Element Element, House House)> On(long mapId)
        {
            foreach (var element in Interactives.ElementsOf(mapId))
            {
                if (TryGetByElement(element.Id, out var house)) yield return (element, house);
            }
        }

        /// <summary>For tests: a marketplace declared by hand.</summary>
        internal static void Declare(House house) => _houses[house.Id] = house;

        /// <summary>For tests: a counter declared by hand.</summary>
        internal static void DeclareCounter(int elementId, int houseId)
            => _counters[elementId] = new Counter { ElementId = elementId, HouseId = houseId, Source = "test" };
    }
}
