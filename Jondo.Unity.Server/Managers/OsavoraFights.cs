using System;
using System.Collections.Generic;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What Osavora's seasons change in its monsters' fights: the season is the fight's scene's,
    /// everybody carries it, and each monster's own spells read it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All of it is the client's data; INFERRED is only what the server keeps for itself. The three
    /// seasons are states -- «[!] Naissance» 6245, «Chasse» 6246, «Repos» 6247 --, and the spells
    /// «Temporada de Nacimientos/Caza/Descanso» (31050-31052) put one on every fighter («a,A»,
    /// shown in the buff bar). No spell of the client casts those three: the server does, when a
    /// fight of Osabana (subarea 1040, the one the seasons' world events 228-230 name) begins.
    /// </para>
    /// <para>
    /// The monsters read it through their starting spells. The lions' «La Temporada del Más
    /// Fuerte» (30900, 30906, 30910), the Gargandias' and the others first cast «[!] Initial
    /// Saison» (30992), whose rows for the client say what it does -- the scene carrying 6245 makes
    /// the caster carry 6245, and so on --; its rows for the server give the caster the three
    /// states (30991, 30994, 30995 grade 2) and then cast 31553, a spell the client does not have.
    /// That one is the server's own, and here it does what the client's rows say the whole spell
    /// does: the season states the scene does not carry come off again.
    /// </para>
    /// <para>
    /// What the season then changes, the monsters' own rows: «La Temporada del Más Fuerte» 31293
    /// gives +1 MP in the Naissance, +20 % final damage in the Chasse, +20 % vitality in the Repos;
    /// and every fifth blow taken (30901's counter, state 6157) each casts its «Reacción de
    /// Temporada» of the season -- 30965 to 30983.
    /// </para>
    /// </remarks>
    public static class OsavoraFights
    {
        /// <summary>Osabana, the subarea the seasons' world events name.</summary>
        public const int Osabana = 1040;

        public const int NaissanceState = 6245, ChasseState = 6246, ReposState = 6247;

        /// <summary>The server's own spell that leaves the caster only the scene's season.</summary>
        public const int KeepTheScenesSeason = 31553;

        private static readonly int[] SeasonStates = { NaissanceState, ChasseState, ReposState };

        /// <summary>The clock the season is read on. Replaceable for the tests.</summary>
        public static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

        public static int StateOf(OsavoraSeasons.Season season) => season switch
        {
            OsavoraSeasons.Season.Naissance => NaissanceState,
            OsavoraSeasons.Season.Chasse => ChasseState,
            _ => ReposState,
        };

        /// <summary>«Temporada de Nacimientos», «de Caza», «de Descanso».</summary>
        public static int SpellOf(OsavoraSeasons.Season season) => season switch
        {
            OsavoraSeasons.Season.Naissance => 31050,
            OsavoraSeasons.Season.Chasse => 31051,
            _ => 31052,
        };

        /// <summary>Osavora, the area Osabana and the Bzzegg Supervisor's Checkpoint are in.</summary>
        public const int Osavora = 96;

        /// <summary>
        /// A fight about to begin: anywhere in Osavora, its scene carries today's season. Returns
        /// the season's spell to cast on everybody, or 0 elsewhere.
        /// </summary>
        /// <remarks>
        /// Not only Osabana: the Kabombz (8048-8051) of Bzzkkbz Zowzrbz (1041) and of the
        /// Bzzegg Supervisor's Checkpoint (1044) start with 31027 grade 1, whose only state is the
        /// season «[!] Initial Saison» copies from the scene, and their death spell 31028 and the
        /// Bzzeggs' 31032 cast nothing unless the caster carries a season or 6306 (which only the
        /// Upset ones' 31278 and grade 3 give). Without a season the boss's phase two, counted on
        /// those deaths, never came.
        /// </remarks>
        public static int SetTheScene(FightInstance fight, int subArea)
        {
            if (subArea != Osabana && Interactives.AreaOfSubArea(subArea) != Osavora) return 0;
            var season = OsavoraSeasons.At(Clock());
            fight.SceneStates.Add(StateOf(season));
            return SpellOf(season);
        }

        /// <summary>
        /// The rows of a spell only the server has, or null for any other: 31553, the season
        /// states the scene does not carry taken off the caster.
        /// </summary>
        public static IReadOnlyList<SpellEffect> ServerRows(FightInstance fight, int spell)
        {
            if (spell != KeepTheScenesSeason || fight == null) return null;
            var rows = new List<SpellEffect>();
            foreach (int state in SeasonStates)
            {
                if (fight.SceneStates.Contains(state)) continue;
                rows.Add(new SpellEffect
                {
                    EffectId = EffectSupport.RemoveState, Value = state, TargetMask = "C", Triggers = "I",
                });
            }
            return rows;
        }
    }
}
