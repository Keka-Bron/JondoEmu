using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The workshop stations of the world: the oven, the anvil, the sewing machine, the magus
    /// tables. Recognised by their graphic, the way <see cref="Resources"/> recognises a wheat.
    /// </summary>
    /// <remarks>
    /// The client knows where each element stands and what it looks like; its type and its skills
    /// come from the server, in the jss. tools/extract_workshops.py crosses every capture with the
    /// client's element dump and writes, per graphic, the type and the craft skills its element
    /// offers. One graphic can offer several: the forgemagus table of Bonta (49506) declares
    /// Forjamagiar, Escultomaguear and Maguear escudo at once.
    ///
    /// A station is not a resource: it does not run out and it is never busy. In the alchemists'
    /// capture another player opens the same alembic while the first one still has it open, and
    /// the server announces both.
    /// </remarks>
    public static class Workshops
    {
        /// <summary>What a station graphic is.</summary>
        public sealed class Station
        {
            public int Gfx { get; init; }
            public int Type { get; init; }
            public IReadOnlyList<int> Skills { get; init; } = Array.Empty<int>();

            /// <summary>jss, use, pr44 or inferred: where the row came from.</summary>
            public string Source { get; init; } = "";
        }

        /// <summary>
        /// The stations as one set: <see cref="Initialize"/> fills a new one on its own thread and
        /// publishes it whole, so whoever reads meanwhile -- a house plaque, a map being sent, a test
        /// running beside -- sees the old stations or the new ones, never a half.
        /// </summary>
        private sealed class Tables
        {
            public readonly Dictionary<int, Station> ByGfx = new();

            /// <summary>Stations a person declared for one element, over its graphic's (content/interactives/workshops.json).</summary>
            public readonly Dictionary<(long MapId, int ElementId), Station> ByElement = new();

            /// <summary>The artisans' book: graphic -> (type, skill). "Consultar", skill 170.</summary>
            public readonly Dictionary<int, (int Type, int Skill)> Books = new();

            /// <summary>Which jobs the book of each map opens.</summary>
            public readonly Dictionary<long, IReadOnlyList<int>> BookJobs = new();
        }

        private static volatile Tables _published = new();
        [ThreadStatic] private static Tables? _building;
        private static Tables Current => _building ?? _published;

        private static Dictionary<int, Station> _byGfx => Current.ByGfx;
        private static Dictionary<(long, int), Station> _byElement => Current.ByElement;
        private static Dictionary<int, (int Type, int Skill)> _books => Current.Books;
        private static Dictionary<long, IReadOnlyList<int>> _bookJobs => Current.BookJobs;

        /// <summary>"Consultar": the skill of every artisans' book.</summary>
        public const int BookSkill = 170;

        public static void Initialize()
        {
            var fresh = new Tables();
            _building = fresh;
            try
            {
                Fill();
            }
            finally
            {
                _building = null;
            }
            _published = fresh;
        }

        /// <summary>The loading proper, into the set being built (<see cref="Tables"/>).</summary>
        private static void Fill()
        {
            FillAuthored();
            string path = Paths.WorkshopsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Workshops] {Path.GetFileName(path)} is missing; no workshop can " +
                                  "be used. Generate it with tools/extract_workshops.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("graficos", out var list)) return;
                foreach (var entry in list.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int gfx)) continue;
                    var skills = new List<int>();
                    foreach (var s in entry.Value.GetProperty("habilidades").EnumerateArray())
                        skills.Add(s.GetInt32());
                    _byGfx[gfx] = new Station
                    {
                        Gfx = gfx,
                        Type = entry.Value.GetProperty("tipo").GetInt32(),
                        Skills = skills,
                        Source = entry.Value.TryGetProperty("fuente", out var f) ? f.GetString() ?? "" : "",
                    };
                }
                if (doc.RootElement.TryGetProperty("libros", out var books))
                {
                    foreach (var entry in books.GetProperty("graficos").EnumerateObject())
                    {
                        if (int.TryParse(entry.Name, out int gfx))
                            _books[gfx] = (entry.Value.GetProperty("tipo").GetInt32(), entry.Value.GetProperty("habilidad").GetInt32());
                    }
                    foreach (var entry in books.GetProperty("mapas").EnumerateObject())
                    {
                        if (!long.TryParse(entry.Name, out long mapId)) continue;
                        var jobs = new List<int>();
                        foreach (var j in entry.Value.EnumerateArray()) jobs.Add(j.GetInt32());
                        _bookJobs[mapId] = jobs;
                    }
                }
                Console.WriteLine($"[Workshops] {_byGfx.Count} station graphics, books on {_bookJobs.Count} maps.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Workshops] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>
        /// The stations declared by hand, element by element: one that offers what its graphic does
        /// not elsewhere -- the workbench by Kaponor's raft, where the raft's planks and beams are
        /// made («Utilizar banco de trabajo», whose only recipes they are), on a graphic the captures
        /// saw as a handyman's.
        /// </summary>
        private static void FillAuthored()
        {
            string path = Paths.ContentFile("interactives/workshops.json");
            if (!File.Exists(path)) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("stations", out var list)) return;
                foreach (var row in list.EnumerateArray())
                {
                    long map = row.GetProperty("map").GetInt64();
                    int element = row.GetProperty("element").GetInt32();
                    var skills = new List<int>();
                    foreach (var s in row.GetProperty("skills").EnumerateArray()) skills.Add(s.GetInt32());
                    _byElement[(map, element)] = new Station
                    {
                        Gfx = row.TryGetProperty("gfx", out var g) ? g.GetInt32() : 0,
                        Type = row.GetProperty("type").GetInt32(),
                        Skills = skills,
                        Source = "content",
                    };
                }
                Console.WriteLine($"[Workshops] {_byElement.Count} station(s) declared by hand.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Workshops] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>The station a graphic is, if it is one.</summary>
        public static bool TryGet(int gfx, out Station station) => _byGfx.TryGetValue(gfx, out station!);

        /// <summary>The stations standing on a map, with the element each one is.</summary>
        public static IEnumerable<(Interactives.Element Element, Station Station)> On(long mapId)
        {
            var stations = _byGfx;
            var declared = _byElement;
            foreach (var element in Interactives.ElementsOf(mapId))
            {
                if (declared.TryGetValue((mapId, element.Id), out var own)) yield return (element, own);
                else if (stations.TryGetValue(element.Gfx, out var station)) yield return (element, station);
            }
        }

        /// <summary>The artisans' books standing on a map, with their type and skill.</summary>
        public static IEnumerable<(Interactives.Element Element, int Type, int Skill)> BooksOn(long mapId)
        {
            var tables = Current;
            if (!tables.BookJobs.ContainsKey(mapId)) yield break;
            foreach (var element in Interactives.ElementsOf(mapId))
            {
                if (tables.Books.TryGetValue(element.Gfx, out var book)) yield return (element, book.Type, book.Skill);
            }
        }

        /// <summary>The jobs whose directory the book of this map opens.</summary>
        public static IReadOnlyList<int> BookJobsOn(long mapId)
            => _bookJobs.TryGetValue(mapId, out var jobs) ? jobs : Array.Empty<int>();
    }
}
