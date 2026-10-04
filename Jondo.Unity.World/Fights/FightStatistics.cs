using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Fights
{
    /// <summary>Where a blow came from, for the end-of-fight statistics.</summary>
    public enum DamageSource
    {
        /// <summary>A cast of the fighter's own: a spell or the weapon.</summary>
        Direct,

        /// <summary>A glyph, a trap or a bomb wall the fighter owns, going off under somebody.</summary>
        Glyph,
    }

    /// <summary>
    /// One person's numbers for the end-of-fight screen, counted as the fight goes.
    /// </summary>
    /// <remarks>
    /// The real server sends them in the jxo behind the jyg, and the shape of that message is
    /// what decides which counters exist here. Read off 30 fights of the Tymador and Cra
    /// captures, each field against the frames of its own fight:
    ///
    ///   damage dealt (f11)   f9 direct casts, f7 glyphs and walls, f2 the summons', f1 what
    ///                        lands on a turn trigger -- Flecha Acosante's 243 -- and f5 the
    ///                        collision damage of a push -- Flecha de Retroceso's 180. Their sum
    ///                        is f4. "bomba de agua y sismobomba": the bombs' 869 + 216 = 1085
    ///                        is f2, the player's 915 splits into f7 = 722 and f9 = 193.
    ///   damage taken (f5)    f9 and f4 the total, f3 the total per turn.
    ///   heals (f10)          f4 given, f5 received, f3 given per turn: Flecha de Expiación's
    ///                        self-heal of 52 sits in both.
    ///   shields (f6)         f1 the points given, f3 per turn: Tymadura's 1400 over 28 turns
    ///                        is the 50 of the capture.
    ///   kills (f2)           f2 and f4, both the enemies that fell.
    ///   points               f3.f4 is AP per turn and f8.f4 MP per turn; f11.f3 is the
    ///                        fighter's own damage per AP spent on the casts that dealt it --
    ///                        100 over 6 for two Flechas de Retroceso -- and f11.f6 the total
    ///                        per turn. The turns are the person's own and those of the summons
    ///                        he drives: the Tymobot fight divides by 28, his 16 and its 12.
    /// </remarks>
    public sealed class FightStatistics
    {
        public int DirectDamage { get; set; }
        public int GlyphDamage { get; set; }
        public int SummonDamage { get; set; }
        public int TriggerDamage { get; set; }
        public int PushDamage { get; set; }

        public int DamageTaken { get; set; }
        public int HealsGiven { get; set; }
        public int HealsReceived { get; set; }
        public int ShieldsGiven { get; set; }

        public int ActionPointsSpent { get; set; }
        public int ActionPointsOnDamage { get; set; }
        public int MovementPointsSpent { get; set; }
        public int TurnsPlayed { get; set; }

        /// <summary>Everything that hurt an enemy on this person's account, summons included.</summary>
        public int TotalDamage => DirectDamage + GlyphDamage + SummonDamage + TriggerDamage + PushDamage;

        /// <summary>What the person did with his own hands: everything but the summons'.</summary>
        public int OwnDamage => DirectDamage + GlyphDamage + TriggerDamage + PushDamage;

        public float PerTurn(int amount) => amount / (float)Math.Max(1, TurnsPlayed);
    }
}
