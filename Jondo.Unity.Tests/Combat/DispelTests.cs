using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// "Retira los embrujos" (132) against real spell rows: each row a spell puts carries its
    /// catalogue "dispellable", and the dispel takes the 1s alone. The rows were put with none,
    /// and a dispel took nothing a class had put.
    /// </summary>
    public class DispelTests
    {
        /// <summary>Iop, Espada Divina: "+10 de daños" on himself and his allies, dispellable 1.</summary>
        private const int EspadaDivina = 13110, EspadaDivinaDamage = 209327;

        /// <summary>Cra, Tiros Potentes: four rows on himself, all dispellable 3 (a strong dispel only).</summary>
        private const int TirosPotentes = 32466, TirosPotentesPower = 424092;

        /// <summary>
        /// Xelor, Momificación: "+2 PM" and state 96 (no placable) on himself, dispellable 2 -- gone
        /// with their caster's death, not with a dispel.
        /// </summary>
        private const int Momificacion = 13260, MomificacionMp = 402319, MomificacionUnmovable = 402318;

        /// <summary>A monster's "Desembruja.": Purificación Fantasmagórica, its single 132.</summary>
        private const int PurificacionFantasmagorica = 1522;

        private static Fighter Fighter(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 1000, CurrentHP = 1000, Level = 100,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
        };

        private static List<SpellEffect> Rows(int spell, params int[] uids)
            => SpellEffects.De(spell, 1).Where(r => uids.Length == 0 || uids.Contains(r.EffectUid)).ToList();

        private static (FightInstance Fight, Fighter Player, Fighter Monster) Board()
        {
            var fight = new FightInstance(1, 1);
            var player = Fighter(1, 0, 300);
            var monster = Fighter(-1, 1, 301);
            fight.AddPlayer(player);
            fight.AddOpponent(monster);
            return (fight, player, monster);
        }

        /// <summary>The caster casts the rows on himself, as the spell's "C" says.</summary>
        private static List<Outcome> SelfCast(FightInstance fight, Fighter caster, int spell, List<SpellEffect> rows)
            => EffectEngine.ResolveEffects(fight, caster, spell, 1, caster, EffectEngine.AlLanzar, fight.RoundNumber,
                                           rows, aimedCell: caster.CellId);

        private static void Dispel(FightInstance fight, Fighter caster, Fighter target)
            => EffectEngine.ResolveEffects(fight, caster, PurificacionFantasmagorica, 1, target, EffectEngine.AlLanzar,
                                           fight.RoundNumber, Rows(PurificacionFantasmagorica), aimedCell: target.CellId);

        [Fact]
        public void A_class_boost_carries_its_catalogue_dispellable()
        {
            var rows = Rows(EspadaDivina, EspadaDivinaDamage);
            if (rows.Count == 0) return;   // no world data
            var (fight, iop, _) = Board();

            var put = SelfCast(fight, iop, EspadaDivina, rows).Select(o => o.Buff).Single(b => b != null);

            Assert.Equal(1, rows[0].Dispellable);
            Assert.Equal(1, put.Dispellable);
        }

        [Fact]
        public void A_dispel_takes_a_class_boost()
        {
            var rows = Rows(EspadaDivina, EspadaDivinaDamage);
            if (rows.Count == 0 || Rows(PurificacionFantasmagorica).Count == 0) return;
            var (fight, iop, monster) = Board();
            SelfCast(fight, iop, EspadaDivina, rows);
            Assert.Contains(iop.Buffs.Puestos, b => b.EffectUid == EspadaDivinaDamage);

            Dispel(fight, monster, iop);

            Assert.DoesNotContain(iop.Buffs.Puestos, b => b.EffectUid == EspadaDivinaDamage);
        }

        /// <summary>A 3 is for a strong dispel, which 132 is not: Tiros Potentes' whole bonus stays.</summary>
        [Fact]
        public void A_dispel_leaves_what_only_a_strong_dispel_takes()
        {
            var rows = Rows(TirosPotentes);
            if (rows.Count == 0 || Rows(PurificacionFantasmagorica).Count == 0) return;
            Assert.All(rows, r => Assert.Equal(3, r.Dispellable));
            var (fight, cra, monster) = Board();
            var put = SelfCast(fight, cra, TirosPotentes, rows).Select(o => o.Buff).Where(b => b != null).ToList();
            Assert.Equal(rows.Count, put.Count);

            Dispel(fight, monster, cra);

            Assert.All(put, b => Assert.Contains(b, cra.Buffs.Puestos));
        }

        /// <summary>
        /// A 2 goes with its caster's death and not with a dispel: the client's own guides write the
        /// 2s as "no pueden retirarse mediante el desembrujo" (the Xelor's Telefrag, the Huppermage's
        /// elemental states), and Momificación's own bonus stays with its state.
        /// </summary>
        [Fact]
        public void A_dispel_leaves_what_goes_with_its_caster_s_death()
        {
            var rows = Rows(Momificacion, MomificacionMp, MomificacionUnmovable);
            if (rows.Count != 2 || Rows(PurificacionFantasmagorica).Count == 0) return;
            Assert.All(rows, r => Assert.Equal(2, r.Dispellable));
            var (fight, xelor, monster) = Board();
            var put = SelfCast(fight, xelor, Momificacion, rows).Select(o => o.Buff).Where(b => b != null).ToList();
            Assert.Equal(2, put.Count);

            Dispel(fight, monster, xelor);

            Assert.All(put, b => Assert.Contains(b, xelor.Buffs.Puestos));
            Assert.True(xelor.Buffs.TieneEstado(96));
        }

        /// <summary>A row of the engine's own, from no spell row, is never dispelled: a wave's arrival state.</summary>
        [Fact]
        public void A_dispel_leaves_an_engine_row()
        {
            if (Rows(PurificacionFantasmagorica).Count == 0) return;
            var (fight, player, monster) = Board();
            var row = player.Buffs.Poner(new Buff
            {
                EffectId = EffectSupport.AddState, Estado = 56, Cuanto = 56, Quien = player.Id,
                Disparador = EffectEngine.AlLanzar, CaducaEnRonda = -1, EmpiezaEnRonda = fight.RoundNumber,
            }, fight.SiguienteEmbrujo);
            player.Buffs.PonerEstado(56);

            Dispel(fight, monster, player);

            Assert.Equal(0, row.Dispellable);
            Assert.Contains(row, player.Buffs.Puestos);
            Assert.True(player.Buffs.TieneEstado(56));
        }

        /// <summary>
        /// A row put outside the engine's spell path -- a handler's, a manager's -- names its spell,
        /// grade and uid, and gets its row's "dispellable" as it is put: the critical list's rows
        /// too. One that carries its own keeps it, and one of no spell row stays at zero.
        /// </summary>
        [Fact]
        public void A_row_naming_its_spell_row_gets_its_dispellable_as_it_is_put()
        {
            var normal = Rows(EspadaDivina, EspadaDivinaDamage);
            if (normal.Count == 0) return;
            var critical = SpellEffects.Criticos(EspadaDivina, 1).First(r => r.EffectId == normal[0].EffectId);
            var who = Fighter(1, 0, 300);
            int n = 0;
            Buff Put(int uid, int spell, int dispellable = 0) => who.Buffs.Poner(new Buff
            {
                EffectId = normal[0].EffectId, EffectUid = uid, HechizoOrigen = spell, NivelOrigen = 1, Quien = 1,
                Dispellable = dispellable, CaducaEnRonda = 3,
            }, () => ++n);

            Assert.Equal(Buffs.DispellableByADispel, Put(EspadaDivinaDamage, EspadaDivina).Dispellable);
            Assert.Equal(critical.Dispellable, Put(critical.EffectUid, EspadaDivina).Dispellable);
            Assert.Equal(Buffs.NeverDispellable, Put(EspadaDivinaDamage, EspadaDivina, Buffs.NeverDispellable).Dispellable);
            Assert.Equal(0, Put(EspadaDivinaDamage, TirosPotentes).Dispellable);   // not that spell's row
            Assert.Equal(0, Put(0, EspadaDivina).Dispellable);
        }

        /// <summary>
        /// Its caster's death takes his 1s and 2s and leaves his 3s: rows an ally of the boss put on
        /// him, named by their spell rows and given no "dispellable" of their own.
        /// </summary>
        [Fact]
        public async Task A_dead_caster_takes_his_1s_and_2s_and_leaves_his_3s()
        {
            var rows = Rows(EspadaDivina, EspadaDivinaDamage)
                .Concat(Rows(Momificacion, MomificacionMp))
                .Concat(Rows(TirosPotentes, TirosPotentesPower)).ToList();
            if (rows.Count != 3) return;
            var fight = new FightInstance(1, 1);
            var player = Fighter(10, 0, 100);
            fight.AddPlayer(player);
            var boss = Monster(-1, 400);
            var ally = Monster(-2, 404);
            fight.AddMonster(boss);
            fight.AddMonster(ally);
            fight.StartFight();
            foreach (var row in rows)
            {
                boss.Buffs.Poner(new Buff
                {
                    EffectId = row.EffectId, EffectUid = row.EffectUid, HechizoOrigen = SpellOf(row.EffectUid), NivelOrigen = 1,
                    Quien = ally.Id, CaducaEnRonda = -1,
                }, fight.SiguienteEmbrujo);
            }
            Assert.Equal(new[] { 1, 2, 3 }, boss.Buffs.Puestos.Where(r => r.Quien == ally.Id).Select(r => r.Dispellable).OrderBy(d => d));

            await FightHandler.TestKillAsync(null, fight, ally, player);

            var left = Assert.Single(boss.Buffs.Puestos, r => r.Quien == ally.Id);
            Assert.Equal(TirosPotentesPower, left.EffectUid);
        }

        private static int SpellOf(int uid)
            => uid == EspadaDivinaDamage ? EspadaDivina : uid == MomificacionMp ? Momificacion : TirosPotentes;

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 140, IsMonster = true, MonsterId = 2849,
            MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
        };

        /// <summary>The monsters weigh a dispel by what it would take: the 1s, and nothing else.</summary>
        [Fact]
        public void The_tactics_weigh_what_a_dispel_takes()
        {
            var divina = Rows(EspadaDivina, EspadaDivinaDamage);
            var tiros = Rows(TirosPotentes);
            var momificacion = Rows(Momificacion, MomificacionMp, MomificacionUnmovable);
            if (divina.Count == 0 || tiros.Count == 0 || momificacion.Count == 0) return;

            var (fight, iop, _) = Board();
            SelfCast(fight, iop, EspadaDivina, divina);
            Assert.True(MonsterTactics.BuffsWorth(iop) > 0);

            var (fight2, other, _) = Board();
            SelfCast(fight2, other, TirosPotentes, tiros);
            SelfCast(fight2, other, Momificacion, momificacion);
            Assert.Equal(0, MonsterTactics.BuffsWorth(other));
        }
    }
}
