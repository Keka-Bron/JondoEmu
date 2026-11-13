using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Soul capture: an empty soul stone worn in its slot, «Captura de Almas» cast, and the souls of
    /// the beaten group in full stones at the end of a won fight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client's own data: the stones (type 83, super type 70, slot 31) give through their 1175
    /// the spell «Piedra de Alma Equipada» (21714…23683), which the fight's attitudes cascade casts:
    /// states 2768 «Pierre d'âme équipée» and the stone's size (2784-2788). Spell 413 asks for 2768
    /// («HS=2768») and puts 2 «Buscador de almas» on its caster for two turns -- «Permite capturar
    /// las almas de las criaturas vencidas al final del combate». A stone's 705 says «Captura de almas
    /// de nivel #3 como máximo», 50 to 1,000, its first number 100; a full stone holds its souls in
    /// 623, «#3 (#2)»: the monster, and its grade.
    /// </para>
    /// <para>
    /// The guides (JOL, Les pierres d'âme; Dofus 2.63): «le dernier monstre du groupe doit mourir
    /// avant que l'état ne se dissipe» -- the state still on at the end --, «une pierre ne peut
    /// capturer un groupe que si son niveau dépasse celui du monstre le plus fort du groupe», and a
    /// capture that cannot be fails. INFERRED: the 705's first number is the chance, in percent; the
    /// stone is spent; the group's archmonsters each go in a «Piedra de alma de archimonstruo» (10418)
    /// -- the Ochre quest asks for each archmonster --, the rest together in a «Piedra de alma llena»
    /// (7010). A dungeon's guardian takes its group along in a «Piedra de alma de guardián de
    /// mazmorra» (10417): Dofus pour les noobs' «Frankenstruche» shows «Gardien de donjon : Haute
    /// Truche» holding five souls, the Haute Truche, a Gruche, two Truchmuches and a Truchtine.
    /// </para>
    /// </remarks>
    public static class SoulCapture
    {
        /// <summary>«Captura de Almas», and the state it puts on its caster.</summary>
        public const int Spell = 413, SeekerState = 2;

        /// <summary>The slot a soul stone is worn in, and their item type.</summary>
        public const int Slot = 31, StoneType = 83;

        /// <summary>«Captura de almas de nivel #3 como máximo».</summary>
        public const int PowerEffect = 705;

        /// <summary>A full stone's soul: «#3 (#2)», the monster and its grade.</summary>
        public const int SoulEffect = 623;

        public const int FullStone = 7010, ArchmonsterStone = 10418, GuardianStone = 10417;

        /// <summary>One beaten monster: its id, its grade (from 1), its level, and what kind it is.</summary>
        public sealed record Soul(int Monster, int Grade, int Level, bool Archmonster, bool Guardian);

        /// <summary>A full stone to hand over: its item and the souls inside.</summary>
        public sealed record Stone(int Item, IReadOnlyList<Soul> Souls)
        {
            /// <summary>Its effects as the database keeps them: one 623 a soul.</summary>
            public IReadOnlyList<Equipment.ItemEffect> Effects
                => Souls.Select(s => new Equipment.ItemEffect(SoulEffect, s.Monster, 0, s.Grade)).ToList();
        }

        /// <summary>A stone's power: the highest level it captures and its chance, from its 705.</summary>
        public static (int MaxLevel, int Chance) PowerOf(IEnumerable<Equipment.ItemEffect> effects)
        {
            foreach (var effect in effects)
                if (effect.Effect == PowerEffect)
                    return ((int)effect.Value, effect.DiceNum > 0 ? (int)Math.Min(100, effect.DiceNum) : 100);
            return (0, 0);
        }

        /// <summary>
        /// What a capture yields: nothing when the strongest soul is past the stone or the roll fails;
        /// otherwise a stone for each archmonster and guardian, and one for the rest.
        /// </summary>
        public static IReadOnlyList<Stone> Capture(IReadOnlyList<Soul> souls, int maxLevel, int chance, Random dice)
        {
            if (souls.Count == 0 || maxLevel <= 0) return Array.Empty<Stone>();
            if (souls.Max(s => s.Level) > maxLevel) return Array.Empty<Stone>();
            if (dice.Next(100) >= chance) return Array.Empty<Stone>();

            var stones = new List<Stone>();
            foreach (var soul in souls)
                if (soul.Archmonster) stones.Add(new Stone(ArchmonsterStone, new[] { soul }));
            var rest = souls.Where(s => !s.Archmonster).OrderByDescending(s => s.Guardian).ToList();
            if (rest.Count > 0) stones.Add(new Stone(rest[0].Guardian ? GuardianStone : FullStone, rest));
            return stones;
        }
    }
}
