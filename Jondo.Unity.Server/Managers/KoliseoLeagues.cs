using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Koliseo's leagues: which one a rating is in, and when a player changes division.
    /// </summary>
    /// <remarks>
    /// ─── Where they come from ─────────────────────────────────────────────────────────────
    ///
    /// The client's own ArenaLeaguesDataRoot, extracted to datos/koliseo_ligas_3.6.10.10.json by
    /// tools/extraer_ligas_koliseo.py: 26 leagues, five tiers of five divisions -- Bronze, Silver,
    /// Gold, Platinum, Diamond -- and Legend, each with the rating it covers. It is the system of
    /// the December 2023 rework (official devblog "Devblog : les ligues", 24/08/2023): the league
    /// is set by the rating alone, and a player goes up and down with his wins and losses.
    ///
    /// ─── The buffer ───────────────────────────────────────────────────────────────────────
    ///
    /// Neighbouring divisions overlap by 50 points (Bronze 1 is 0 to 419, Bronze 2 is 370 to
    /// 569). That is the devblog's "tampon": crossing a line is only confirmed by the next fight.
    /// So a placed player KEEPS his division while his rating stays inside its bounds, and only
    /// when it leaves them does he move to the division the rating is in now. Placement, with no
    /// division to keep, takes the first one whose top the rating does not pass.
    /// </remarks>
    public static class KoliseoLeagues
    {
        public const string DataFile = "koliseo_ligas_3.6.10.10.json";

        /// <summary>No league: a player who has not finished his placement fights.</summary>
        public const int None = -1;

        public sealed record League(int Id, int NameId, string Tier, int Division, int OrnamentId,
                                    int Low, int High, bool Last);

        private static readonly object Gate = new object();
        private static List<League>? _all;

        /// <summary>Every league, from the lowest rating up.</summary>
        public static IReadOnlyList<League> All
        {
            get
            {
                lock (Gate) return _all ??= Load();
            }
        }

        private static List<League> Load()
        {
            var leagues = new List<League>();
            string path = Paths.Resolve(DataFile);
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Koliseo] {DataFile} is missing: nobody can be placed in a league.");
                return leagues;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var l in doc.RootElement.GetProperty("ligas").EnumerateArray())
            {
                leagues.Add(new League(
                    l.GetProperty("id").GetInt32(), l.GetProperty("nameId").GetInt32(),
                    l.GetProperty("tier").GetString() ?? "", l.GetProperty("division").GetInt32(),
                    l.GetProperty("ornamentId").GetInt32(), l.GetProperty("low").GetInt32(),
                    l.GetProperty("high").GetInt32(), l.GetProperty("last").GetBoolean()));
            }
            leagues.Sort((a, b) => a.Low != b.Low ? a.Low.CompareTo(b.Low) : a.Id.CompareTo(b.Id));
            return leagues;
        }

        /// <summary>The league with that id, or null.</summary>
        public static League? ById(int id)
        {
            foreach (var league in All) if (league.Id == id) return league;
            return null;
        }

        /// <summary>Where a league sits from the bottom, 0 for Bronze 1; -1 for none.</summary>
        public static int Rank(int leagueId)
        {
            var all = All;
            for (int i = 0; i < all.Count; i++) if (all[i].Id == leagueId) return i;
            return -1;
        }

        /// <summary>The higher of two leagues, either possibly <see cref="None"/>.</summary>
        public static int Higher(int a, int b) => Rank(a) >= Rank(b) ? a : b;

        /// <summary>The league a rating is placed in: the first whose top it does not pass.</summary>
        public static int Place(int rating)
        {
            var all = All;
            if (all.Count == 0) return None;
            foreach (var league in all)
                if (league.Last || rating <= league.High) return league.Id;
            return all[^1].Id;
        }

        /// <summary>
        /// The league of a placed player after his rating changed: the same one while the rating
        /// is inside it, else the nearest one the rating is in, up or down.
        /// </summary>
        public static int Move(int current, int rating)
        {
            var all = All;
            int at = Rank(current);
            if (at < 0) return Place(rating);
            var here = all[at];
            if (rating >= here.Low && (here.Last || rating <= here.High)) return current;

            if (rating > here.High)
            {
                for (int i = at + 1; i < all.Count; i++)
                    if (all[i].Last || rating <= all[i].High) return all[i].Id;
                return all[^1].Id;
            }
            for (int i = at - 1; i >= 0; i--)
                if (rating >= all[i].Low) return all[i].Id;
            return all[0].Id;
        }

        /// <summary>For tests: read the file again.</summary>
        internal static void Reload()
        {
            lock (Gate) _all = null;
        }
    }
}
