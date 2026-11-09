using System.Linq;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A shield's points live on the fighter and its row (1040) only draws them: whichever way the
    /// row goes -- dispelled, taken off with its spell, run out, replaced by a recast that does not
    /// stack -- its points go with it. Only the recast took them off before, and a dispelled
    /// shield kept absorbing hits, unseen, for the rest of the fight.
    /// </summary>
    public class ShieldRowTests
    {
        private static int _next;

        private static Buff Row(int points, int spell, int maxStacks = -1, int dispellable = 1) => new()
        {
            EffectId = Fighter.ShieldRow, Cuanto = points, HechizoOrigen = spell, NivelOrigen = 1, Quien = 1,
            CaducaEnRonda = 3, EmpiezaEnRonda = 1, MaxStacks = maxStacks, Dispellable = dispellable,
        };

        private static Fighter Shielded(params Buff[] rows)
        {
            var who = new Fighter { Id = 1, MaxHP = 1000, CurrentHP = 1000 };
            foreach (var row in rows)
            {
                who.Escudar(row.Cuanto, row.CaducaEnRonda);
                who.Buffs.Poner(row, () => ++_next);
            }
            return who;
        }

        [Fact]
        public void A_dispelled_shield_takes_its_points()
        {
            var who = Shielded(Row(300, 100));
            Assert.Equal(300, who.PuntosDeEscudo);
            who.Buffs.QuitarLosDesembrujables();
            Assert.Equal(0, who.PuntosDeEscudo);
        }

        [Fact]
        public void A_shield_taken_off_with_its_spell_takes_its_points_and_no_other()
        {
            var who = Shielded(Row(300, 100), Row(200, 200));
            who.Buffs.QuitarDelHechizo(100);
            Assert.Equal(200, who.PuntosDeEscudo);
        }

        [Fact]
        public void A_spent_shield_gives_nothing_back_when_it_goes()
        {
            var who = Shielded(Row(300, 100));
            Assert.Equal(0, who.PasarPorElEscudo(250));
            who.Buffs.QuitarDelHechizo(100);
            Assert.Equal(0, who.PuntosDeEscudo);
        }

        [Fact]
        public void A_recast_that_does_not_stack_counts_the_points_once()
        {
            var who = Shielded(Row(350, 100, maxStacks: 1), Row(350, 100, maxStacks: 1));
            Assert.Equal(350, who.PuntosDeEscudo);
        }

        /// <summary>
        /// A critical cast's rows pile up under the normal ones': Lanzamiento de Pala's "-4
        /// alcance" holds one (maxStack 1) whether the cast was critical or not -- its critical
        /// list carries other uids (211002 against 211001), and the two lived together.
        /// </summary>
        [Fact]
        public void A_critical_row_replaces_the_normal_one_under_a_cap_of_one()
        {
            const int LanzamientoDePala = 13330;
            var normal = Jondo.Unity.Server.Managers.SpellEffects.De(LanzamientoDePala, 1).First(e => e.EffectId == 116);
            var critical = Jondo.Unity.Server.Managers.SpellEffects.Criticos(LanzamientoDePala, 1).First(e => e.EffectId == 116);
            Assert.NotEqual(normal.EffectUid, critical.EffectUid);
            var who = new Fighter { Id = 2, MaxHP = 1000, CurrentHP = 1000 };
            Buff Row(int uid) => new() { EffectId = 116, EffectUid = uid, HechizoOrigen = LanzamientoDePala, NivelOrigen = 1, Quien = 1,
                                         MaxStacks = normal.MaxStack, Cuanto = -4, CaducaEnRonda = 3, EmpiezaEnRonda = 1 };
            who.Buffs.Poner(Row(normal.EffectUid), () => ++_next);
            who.Buffs.Poner(Row(critical.EffectUid), () => ++_next);
            Assert.Single(who.Buffs.Puestos, r => r.EffectId == 116);
        }

        [Fact]
        public void An_undispellable_shield_stays()
        {
            var who = Shielded(Row(300, 100, dispellable: 3));
            who.Buffs.QuitarLosDesembrujables();
            Assert.Equal(300, who.PuntosDeEscudo);
        }
    }
}
