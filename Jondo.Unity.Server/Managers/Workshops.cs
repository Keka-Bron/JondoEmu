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

        private static readonly Dictionary<int, Station> _byGfx = new Dictionary<int, Station>();

        /// <summary>The artisans' book: graphic -> (type, skill). "Consultar", skill 170.</summary>
        private static readonly Dictionary<int, (int Type, int Skill)> _books = new Dictionary<int, (int, int)>();

        /// <summary>Which jobs the book of each map opens.</summary>
        private static readonly Dictionary<long, IReadOnlyList<int>> _bookJobs = new Dictionary<long, IReadOnlyList<int>>();

        /// <summary>"Consultar": the skill of every artisans' book.</summary>
        public const int BookSkill = 170;

        public static int Count => _byGfx.Count;
        public static int BookMaps => _bookJobs.Count;

        public static void Initialize()
        {
            _byGfx.Clear();
            _books.Clear();
            _bookJobs.Clear();
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

        /// <summary>The station a graphic is, if it is one.</summary>
        public static bool TryGet(int gfx, out Station station) => _byGfx.TryGetValue(gfx, out station!);

        /// <summary>The stations standing on a map, with the element each one is.</summary>
        public static IEnumerable<(Interactives.Element Element, Station Station)> On(long mapId)
        {
            foreach (var element in Interactives.ElementsOf(mapId))
            {
                if (_byGfx.TryGetValue(element.Gfx, out var station)) yield return (element, station);
            }
        }

        /// <summary>The artisans' books standing on a map, with their type and skill.</summary>
        public static IEnumerable<(Interactives.Element Element, int Type, int Skill)> BooksOn(long mapId)
        {
            if (!_bookJobs.ContainsKey(mapId)) yield break;
            foreach (var element in Interactives.ElementsOf(mapId))
            {
                if (_books.TryGetValue(element.Gfx, out var book)) yield return (element, book.Type, book.Skill);
            }
        }

        /// <summary>The jobs whose directory the book of this map opens.</summary>
        public static IReadOnlyList<int> BookJobsOn(long mapId)
            => _bookJobs.TryGetValue(mapId, out var jobs) ? jobs : Array.Empty<int>();

        /// <summary>For tests: a station graphic declared by hand.</summary>
        internal static void Declare(int gfx, int type, params int[] skills)
            => _byGfx[gfx] = new Station { Gfx = gfx, Type = type, Skills = skills, Source = "test" };
    }
}
