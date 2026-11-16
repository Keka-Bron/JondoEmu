using System;
using System.Linq;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What the temporal anomalies drop: the anomaly fragments, and an uchronic elixir's part in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client's data carries the fragment as one row of every monster's global table, 1 to 20 %
    /// on «(HA=50|HS=3383)&amp;Az=1&amp;Pm!28049666»: as written, it dropped at 1 % and only under the
    /// minor elixir, and nowhere in the anomaly itself, whose rooms are no anomaly's zone (subarea
    /// 916). The rates are the server's and are not in the data; these are Dofus pour les noobs'
    /// («Les Anomalies Temporelles», updated 05/10/2026), with nothing measured to set against them:
    /// </para>
    /// <list type="bullet">
    ///   <item>«Se drop sur les Chronomorphes des anomalies à un taux de 40% de base» -- in the
    ///     guardian's room, off the brutoformes, cublaformes and kaxaformes;</item>
    ///   <item>«Se drop également sur les monstres des zones affectés par les anomalies à un taux de
    ///     base de 1%», 5, 10 and 20 % under the minor, improved and major elixirs;</item>
    ///   <item>«Les challenges, la prospection et le bonus de zone n'augmentent pas ce taux»;</item>
    ///   <item>«le nombre de fragments d'anomalies obtenables est de 3 par personnage par combat».</item>
    /// </list>
    /// <para>
    /// And the elixir is the group's: «Vous pouvez consommer l'Élixir sur un seul personnage du
    /// groupe, les autres personnages bénéficieront également des bonus de drop […] c'est le plus
    /// puissant qui est pris en compte». That goes for the guardians' rows too -- their temporal
    /// loops and the Bébémoth relic --, which name an elixir by its state (HS 3383 to 3385) or its
    /// alteration (HA 50 to 52, paired with them in the same rows): each is answered with the
    /// strongest elixir of the side, not the one of who rolls.
    /// </para>
    /// </remarks>
    public static class AnomalyLoot
    {
        /// <summary>The anomaly fragment.</summary>
        public const int Fragment = 26870;

        /// <summary>The map the fragment's row leaves out (Pm!28049666).</summary>
        public const long LeftOutMap = 28049666;

        /// <summary>The most fragments one character takes from one fight.</summary>
        public const int MostPerFight = 3;

        /// <summary>Off a chronomorph, in the guardian's room.</summary>
        public const double ChronomorphChance = 40;

        /// <summary>Off a monster of an anomaly's zone, by the side's elixir: none, minor, improved, major.</summary>
        public static readonly double[] ZoneChance = { 1, 5, 10, 20 };

        /// <summary>The uchronic elixirs' states and alterations, minor to major.</summary>
        public static readonly int[] ElixirStates = { 3383, 3384, 3385 };
        public static readonly int[] ElixirAlterations = { 50, 51, 52 };

        /// <summary>The strongest elixir on a character's side: 0 for none, 1 minor, 2 improved, 3 major.</summary>
        public static int SideElixir(FightInstance fight, long characterId)
        {
            int best = 0;
            foreach (var fighter in fight.Aliados(characterId))
                for (int tier = ElixirStates.Length; tier > best; tier--)
                    if (fighter.Buffs.TieneEstado(ElixirStates[tier - 1])) { best = tier; break; }
            return best;
        }

        /// <summary>
        /// Which elixir a condition names -- HS 3383 to 3385, HA 50 to 52 -- from 1 to 3, or null
        /// for any other condition.
        /// </summary>
        public static int? ElixirNamed(string code, long value)
        {
            int[] list = code switch { "HS" => ElixirStates, "HA" => ElixirAlterations, _ => null };
            if (list == null) return null;
            int at = Array.IndexOf(list, (int)value);
            return at < 0 ? null : at + 1;
        }

        /// <summary>
        /// A fragment's chance off one monster, or zero: <paramref name="inRoom"/> a guardian's room,
        /// <paramref name="inZone"/> an open anomaly's zone, <paramref name="elixir"/> the side's.
        /// </summary>
        public static double FragmentChance(int monsterId, bool inRoom, bool inZone, int elixir)
        {
            if (inRoom) return AnomalyPlaces.IsChronomorph(monsterId) ? ChronomorphChance : 0;
            return inZone ? ZoneChance[Math.Clamp(elixir, 0, ZoneChance.Length - 1)] : 0;
        }

        /// <summary>Whether a fight is fought where fragments drop, and how: in a guardian's room, or in an open anomaly's zone.</summary>
        public static (bool InRoom, bool InZone) WhereFought(long roleplayMapId)
        {
            if (roleplayMapId <= 0 || roleplayMapId == LeftOutMap) return (false, false);
            bool room = AnomalyPlaces.ByRoom(roleplayMapId) != null;
            bool zone = !room && Anomalies.TryGet(DatabaseManager.SubAreaOfMap(roleplayMapId), out _);
            return (room, zone);
        }
    }
}
