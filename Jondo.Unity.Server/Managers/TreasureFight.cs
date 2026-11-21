using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The fight at a treasure hunt's end: the Treasure (monster 3724) and the four Vigicofres it
    /// summons (7024-7027), one of them the «coffre sur pattes».
    /// </summary>
    /// <remarks>
    /// DofusPourLesNoobs (Les chasses aux trésors): the Treasure «est invulnérable», with 5 to 10
    /// life by its grade -- the client's grades say the same --; «Frapper ce dernier avec une
    /// ligne de dommage retire 1 PV au Trésor. Il peut changer de place à chaque coup»; a wrong
    /// one, «environ 500 dégâts neutres, et le Trésor se soigne de 1 PV. Au troisième échec, le
    /// joueur est OS». The Vigicofres are invulnerable too. None of it is in the client's data --
    /// the Treasure's four spells only summon the four chests -- so the server holds it: which
    /// chest is the one on legs, drawn again after every right blow, and each player's mistakes.
    /// The Treasure summons each chest once: one already standing is not summoned again.
    /// </remarks>
    public static class TreasureFight
    {
        public const int Treasure = 3724;
        public static readonly int[] Chests = { 7024, 7025, 7026, 7027 };

        /// <summary>What a wrong chest does to whoever hit it: «environ 500 dégâts neutres».</summary>
        public const int WrongBlow = 500;

        /// <summary>The mistake that kills: «Au troisième échec, le joueur est OS».</summary>
        public const int FatalMistake = 3;

        /// <summary>
        /// «Acalicanto» (22387): the look the Treasure's own 21349 puts on the chest on legs, the
        /// one the client draws with legs (its 149, appearance 1877).
        /// </summary>
        public const int LegsSpell = 22387;

        public enum Outcome { Nothing, Right, Wrong, Fatal }

        private sealed class State
        {
            public long OnLegs;
            public readonly Dictionary<long, int> Mistakes = new();
        }

        private static readonly ConditionalWeakTable<FightInstance, State> _states = new();

        public static bool IsChest(int monster) => Chests.Contains(monster);

        /// <summary>Whether a blow on this fighter is the treasure's to judge: the Treasure or one of its chests.</summary>
        public static bool Guards(Fighter target) => target != null && (target.MonsterId == Treasure || IsChest(target.MonsterId));

        /// <summary>The Treasure of a fighter's side, alive.</summary>
        public static Fighter? TreasureOf(FightInstance fight, Fighter onItsSide)
            => fight.Bando(onItsSide.TeamId).FirstOrDefault(f => f.MonsterId == Treasure && f.IsAlive);

        /// <summary>Whether the Treasure would summon a chest already standing: it does not.</summary>
        public static bool AlreadyOut(FightInstance fight, Fighter summoner, int monster)
            => summoner.MonsterId == Treasure && IsChest(monster)
               && fight.Bando(summoner.TeamId).Any(f => f.MonsterId == monster && f.IsAlive);

        /// <summary>
        /// A blow on the Treasure or a chest, judged: nothing on the Treasure; on the chest on legs,
        /// right -- and another drawn --; on another, a mistake, the third one fatal.
        /// </summary>
        public static Outcome Judge(FightInstance fight, Fighter caster, Fighter target, Random dice)
        {
            if (!IsChest(target.MonsterId)) return Outcome.Nothing;
            var chests = fight.Bando(target.TeamId).Where(f => IsChest(f.MonsterId) && f.IsAlive).ToList();
            if (chests.Count == 0) return Outcome.Nothing;

            // The chest the player sees on legs is the one: the Treasure's 21349 put Acalicanto's
            // look on it. Drawn by the server only when none wears it -- 21349 can name 7027 when
            // it is not out yet --, or after a right blow (FightHandler moves the look after it).
            var state = _states.GetOrCreateValue(fight);
            var legged = chests.FirstOrDefault(WearsTheLegs);
            if (legged != null) state.OnLegs = legged.Id;
            else if (!chests.Any(c => c.Id == state.OnLegs)) state.OnLegs = chests[dice.Next(chests.Count)].Id;

            if (target.Id == state.OnLegs)
            {
                state.OnLegs = chests[dice.Next(chests.Count)].Id;
                return Outcome.Right;
            }

            state.Mistakes.TryGetValue(caster.Id, out int made);
            state.Mistakes[caster.Id] = ++made;
            return made >= FatalMistake ? Outcome.Fatal : Outcome.Wrong;
        }

        /// <summary>Whether a chest wears Acalicanto's look, the legs the client draws on it.</summary>
        public static bool WearsTheLegs(Fighter chest) => chest.Buffs.Puestos.Any(row => row.HechizoOrigen == LegsSpell);

        /// <summary>Which chest is on legs now, for the look to follow it and for the tests.</summary>
        internal static long OnLegs(FightInstance fight) => _states.TryGetValue(fight, out var state) ? state.OnLegs : 0;
    }
}
