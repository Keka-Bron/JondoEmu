using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Fights a reply to an NPC starts, with who is in front, who fights beside the player and on
    /// which arena (content/fights/npc_fights.json).
    /// </summary>
    /// <remarks>
    /// The tutorial's last test: the celestial chafers of the combat room are NPCs, and «Empezar el
    /// combate contra los tres monstruos» (effect 911 beside it) puts the player on 241441797
    /// against a chafer explorer and two pikemen, with Master Darmas (monster 8018) on his side --
    /// fighter -1, 12 AP, 80 % resistances, in the jxg of the tutorial capture.
    /// </remarks>
    public static class NpcFights
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "fights/npc_fights.json";

        /// <summary>One fight a reply starts.</summary>
        public sealed class Fight
        {
            public int NpcId { get; init; }
            public long MapId { get; init; }
            public long Reply { get; init; }
            public long ArenaMapId { get; init; }
            public IReadOnlyList<(int Monster, int Grade)> Monsters { get; init; } = Array.Empty<(int, int)>();
            public IReadOnlyList<(int Monster, int Grade)> Allies { get; init; } = Array.Empty<(int, int)>();

            /// <summary>Whether the fight is lost when one of the allies falls («alliesMustLive»).</summary>
            public bool AlliesMustLive { get; init; }

            /// <summary>Where a win leaves the character, if not where it fought («winMap», «winCell»).</summary>
            public long WinMapId { get; init; }

            /// <summary>The cell of <see cref="WinMapId"/>.</summary>
            public int WinCellId { get; init; }
            /// <summary>
            /// A mark the win notes among the character's used elements («marks»), which a reply's
            /// «afterElement» asks for: the heaps of filings of «Las primeras impresiones son
            /// engañosas» give their handful only once their moguis are beaten. Negative, so it
            /// never meets an element of a map.
            /// </summary>
            public int Marks { get; init; }

            /// <summary>
            /// Whether the monsters come at the grade of the character's level instead of the one written
            /// («byLevel»): the recruiting captains of Bonta and Brakmar duel whoever comes, and their
            /// monsters (6851, 6852) have one grade every 20 levels, from 20 to 200.
            /// </summary>
            public bool ByLevel { get; init; }

            /// <summary>
            /// The monster the character fights as, or 0 («incarnates»: { monster, grade,
            /// invulnerable }): «Barnabé en el espacio» makes him the Entrenador (3985) -- one life
            /// point, three AP, no MP, the gladiators' three calls, Esponja and Bronca --, «un
            /// épouvantail qui a 1PV 0PM 3PA mais vous êtes invulnérable» (Dofus pour les noobs).
            /// </summary>
            public int Incarnates { get; init; }

            /// <summary>The grade of <see cref="Incarnates"/>, 1-based.</summary>
            public int IncarnationGrade { get; init; } = 1;

            /// <summary>Whether the incarnation can take no blow (the client's state 269).</summary>
            public bool IncarnationInvulnerable { get; init; }

            /// <summary>
            /// Whether the character plays the allies by hand («alliesPlayed»): Barnabé,
            /// «contrôlable depuis la màj 2.70» (Fighter.IsPlayedAlly).
            /// </summary>
            public bool AlliesPlayed { get; init; }
        }

        private static List<Fight>? _fights;
        private static readonly object _lock = new();

        /// <summary>The fight this reply to this NPC starts here, or null.</summary>
        public static Fight? For(int npcId, long mapId, long reply)
        {
            Load();
            foreach (var fight in _fights!)
            {
                if (fight.NpcId == npcId && fight.Reply == reply && (fight.MapId == 0 || fight.MapId == mapId)) return fight;
            }
            return null;
        }

        /// <summary>For the tests: read the file again.</summary>
        internal static void Forget()
        {
            lock (_lock) _fights = null;
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (_fights != null) return;
                var fights = new List<Fight>();
                try
                {
                    string path = Paths.ContentFile(AuthoredFile);
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var row in doc.RootElement.GetProperty("fights").EnumerateArray())
                        {
                            bool incarnates = row.TryGetProperty("incarnates", out var incarnation)
                                              && incarnation.ValueKind == JsonValueKind.Object;
                            fights.Add(new Fight
                            {
                                NpcId = (int)Long(row, "npc"),
                                MapId = Long(row, "map"),
                                Reply = Long(row, "reply"),
                                ArenaMapId = Long(row, "arena"),
                                Monsters = Members(row, "monsters"),
                                Allies = Members(row, "allies"),
                                AlliesMustLive = row.TryGetProperty("alliesMustLive", out var must) && must.ValueKind == JsonValueKind.True,
                                WinMapId = Long(row, "winMap"),
                                WinCellId = (int)Long(row, "winCell"),
                                Marks = (int)Long(row, "marks"),
                                ByLevel = row.TryGetProperty("byLevel", out var byLevel) && byLevel.ValueKind == JsonValueKind.True,
                                Incarnates = incarnates ? (int)Long(incarnation, "monster") : 0,
                                IncarnationGrade = incarnates ? Math.Max(1, (int)Long(incarnation, "grade")) : 1,
                                IncarnationInvulnerable = incarnates && incarnation.TryGetProperty("invulnerable", out var invulnerable)
                                                          && invulnerable.ValueKind == JsonValueKind.True,
                                AlliesPlayed = row.TryGetProperty("alliesPlayed", out var played) && played.ValueKind == JsonValueKind.True,
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                }
                _fights = fights;
            }
        }

        private static List<(int, int)> Members(JsonElement row, string name)
        {
            var found = new List<(int, int)>();
            if (!row.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return found;
            foreach (var member in list.EnumerateArray())
            {
                int monster = (int)Long(member, "monster");
                if (monster != 0) found.Add((monster, (int)Long(member, "grade")));
            }
            return found;
        }

        private static long Long(JsonElement row, string name)
            => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;
    }
}
