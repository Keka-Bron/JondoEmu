using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The siphon fight of «Memories of an amnesiac» (1749, step 11169). The tool bag (4516) holds
    /// the fight's script in its behaviour spell, «Siphon Defence» (6853 grade 1): the two
    /// insensitive shorrokhs (4520) are invulnerable (56) for good, the faulty turret (4519) is
    /// broken (426, and its «Sea Wave» 6851 wants HS!426). At the bag's turn end (6853 grade 2) a
    /// player next to it takes the tools (430), and every player holding them casts «Turret
    /// Repair» (6855) round himself: the turret next to him loses 426. The repaired turret's
    /// «Sea Wave» takes the 56 off the shorrokh it hits (952) and wounds it. Dofus pour les noobs
    /// says the same: end a turn by the bag for a state, then one by a broken turret; only the
    /// turrets can hit the shorrokhs.
    /// </summary>
    [Collection("MapManager")]
    public class SiphonTurretRepairTests
    {
        private const int Bag = 4516, Faulty = 4519, Shorrokh = 4520;
        private const int Invulnerable = 56, Broken = 426, Tools = 430;

        private static Fighter Mob(long id, int template, int cell, int hp = 3000) => new()
        {
            Id = id, CellId = cell, MaxHP = hp, CurrentHP = hp, Level = 200, IsMonster = true,
            MonsterId = template, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
        };

        private static (FightInstance Fight, Fighter Me, Fighter Bag, Fighter Turret, Fighter Shorrokh) Board(
            int meCell, int bagCell, int turretCell, int shorrokhCell)
        {
            var fight = new FightInstance(1, 1);
            var me = new Fighter
            {
                Id = 10, CellId = meCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var bag = Mob(-1, Bag, bagCell, 5500);
            var turret = Mob(-2, Faulty, turretCell);
            var shorrokh = Mob(-3, Shorrokh, shorrokhCell);
            fight.AddPlayer(me); fight.AddPlayer(bag); fight.AddPlayer(turret); fight.AddOpponent(shorrokh);
            me.CellId = meCell; bag.CellId = bagCell; turret.CellId = turretCell; shorrokh.CellId = shorrokhCell;
            fight.StartFight();
            bag.Conducta = (6853, 1);
            return (fight, me, bag, turret, shorrokh);
        }

        [Fact]
        public async Task The_bag_s_script_breaks_the_turret_and_shields_the_shorrokhs()
        {
            if (SpellEffects.De(6853, 1).Count == 0) return;   // no spell data
            var around = MapGeometry.GetNeighbors(300).ToList();
            var (fight, me, bag, turret, shorrokh) = Board(300, around[0], around[1], 400);

            await FightHandler.LanzarLaConductaAsync(null, fight, bag);

            Assert.True(shorrokh.Buffs.TieneEstado(Invulnerable));
            Assert.True(turret.Buffs.TieneEstado(Broken));
        }

        [Fact]
        public async Task The_tools_taken_far_from_any_turret_repair_one_at_the_next_bag_turn()
        {
            if (SpellEffects.De(6853, 2).Count == 0 || SpellEffects.De(6855, 1).Count == 0) return;
            var around = MapGeometry.GetNeighbors(300).ToList();
            // The turret is two cells away: nothing to repair when the player takes the tools.
            int far = MapGeometry.GetNeighbors(around[1]).First(c => c != 300 && !around.Contains(c));
            var (fight, me, bag, turret, shorrokh) = Board(300, around[0], far, 450);
            await FightHandler.LanzarLaConductaAsync(null, fight, bag);

            await FightHandler.DispararAsync(null, fight, bag, EffectEngine.AlAcabarElTurno);
            Assert.True(me.Buffs.TieneEstado(Tools));
            Assert.True(turret.Buffs.TieneEstado(Broken));

            // The player walks next to the turret; at the bag's next turn end he repairs it.
            me.CellId = around[1];
            await FightHandler.DispararAsync(null, fight, bag, EffectEngine.AlAcabarElTurno);
            Assert.False(turret.Buffs.TieneEstado(Broken));
            Assert.False(me.Buffs.TieneEstado(Tools));
        }

        /// <summary>Two bag turn ends: the tools by the bag, then the repair by the turret.</summary>
        private static async Task<(FightInstance Fight, Fighter Me, Fighter Turret, Fighter Shorrokh)> Repaired()
        {
            var around = MapGeometry.GetNeighbors(300).ToList();
            int far = MapGeometry.GetNeighbors(around[1]).First(c => c != 300 && !around.Contains(c));
            // Within the «Sea Wave»'s 1 to 10 cells of the turret, on the far side from the player.
            int shorrokhCell = Enumerable.Range(0, MapGeometry.MaxCells)
                .First(c => MapGeometry.Distance(far, c) == 5 && MapGeometry.Distance(300, c) > 5);
            var (fight, me, bag, turret, shorrokh) = Board(300, around[0], far, shorrokhCell);
            await FightHandler.LanzarLaConductaAsync(null, fight, bag);
            await FightHandler.DispararAsync(null, fight, bag, EffectEngine.AlAcabarElTurno);
            me.CellId = around[1];
            await FightHandler.DispararAsync(null, fight, bag, EffectEngine.AlAcabarElTurno);
            return (fight, me, turret, shorrokh);
        }

        [Fact]
        public async Task The_repaired_turret_s_sea_wave_strips_the_shorrokh_s_invulnerability()
        {
            if (SpellEffects.De(6851, 1).Count == 0 || SpellEffects.De(6855, 1).Count == 0) return;
            var (fight, me, turret, shorrokh) = await Repaired();
            Assert.False(turret.Buffs.TieneEstado(Broken));
            Assert.True(shorrokh.Buffs.TieneEstado(Invulnerable));

            int hp = shorrokh.CurrentHP;
            await FightHandler.LanzarPorOrdenAsync(null, fight, turret, 6851, 1, shorrokh, shorrokh.CellId,
                                                   EffectEngine.AlLanzar, SpellEffects.De(6851, 1));

            Assert.False(shorrokh.Buffs.TieneEstado(Invulnerable));
            Assert.True(shorrokh.CurrentHP < hp);
        }

        /// <summary>
        /// Played by the server, the repaired turret aims its «Sea Wave» at the shorrokh, the
        /// invulnerable one included: the 952 that strips the state is worth the blow behind it.
        /// While it is broken, HS!426 keeps the spell out of its hands.
        /// </summary>
        [Fact]
        public async Task The_server_played_turret_shoots_the_shorrokh_once_repaired()
        {
            if (SpellEffects.De(6851, 1).Count == 0 || SpellEffects.De(6855, 1).Count == 0) return;
            MobSpawnManager.EnsureMonsterData();
            var (fight, me, turret, shorrokh) = await Repaired();
            turret.SpellIds.Add(6851); turret.SpellGrades[6851] = 1;
            turret.CurrentAP = turret.MaxAP = 2;
            turret.CurrentMP = turret.MaxMP = 0;
            Assert.True(SpellCriteria.Allows(turret, 6851, 1));

            var spells = FightHandler.TacticsOf(turret);
            var action = MonsterTactics.Next(FightHandler.BoardOf(fight), turret, spells, new System.Collections.Generic.List<MonsterTactics.Spell>());
            Assert.Equal(6851, action?.Spell.Id);
            Assert.Same(shorrokh, action!.Target);
        }

        /// <summary>
        /// The foes that are not invulnerable stand nearer and take the same blow: the turret still
        /// aims at the shorrokh, because only that cast also takes its 56 off (952).
        /// </summary>
        [Fact]
        public async Task The_turret_prefers_the_shorrokh_it_can_strip_over_nearer_foes()
        {
            if (SpellEffects.De(6851, 1).Count == 0 || SpellEffects.De(6855, 1).Count == 0) return;
            MobSpawnManager.EnsureMonsterData();
            var (fight, me, turret, shorrokh) = await Repaired();
            turret.SpellIds.Add(6851); turret.SpellGrades[6851] = 1;
            turret.CurrentAP = turret.MaxAP = 2;
            turret.CurrentMP = turret.MaxMP = 0;
            int a = MapGeometry.GetNeighbors(turret.CellId).First(c => c != me.CellId && c != shorrokh.CellId && !fight.Todos.Any(f => f.CellId == c));
            int b = MapGeometry.GetNeighbors(a).First(c => c != turret.CellId && !fight.Todos.Any(f => f.CellId == c) && c != me.CellId);
            var near1 = Mob(-4, 4448, a); var near2 = Mob(-5, 4449, b);
            fight.AddOpponent(near1); fight.AddOpponent(near2);
            near1.CellId = a; near2.CellId = b;

            var spells = FightHandler.TacticsOf(turret);
            var action = MonsterTactics.Next(FightHandler.BoardOf(fight), turret, spells, new System.Collections.Generic.List<MonsterTactics.Spell>());
            Assert.Equal(6851, action?.Spell.Id);
            Assert.Same(shorrokh, action!.Target);

            // Not a tie won by order: the stripped state is worth more than the same blow on a foe.
            var options = MonsterTactics.Options(FightHandler.BoardOf(fight), turret, spells);
            double onShorrokh = options.Where(o => o.Target == shorrokh).Max(o => o.Score);
            double onOthers = options.Where(o => o.Target == near1 || o.Target == near2).Max(o => o.Score);
            Assert.True(onShorrokh > onOthers, $"{onShorrokh} vs {onOthers}");

            // Once its invulnerability is gone there is nothing to strip: no preference left.
            shorrokh.Buffs.QuitarEstado(Invulnerable);
            options = MonsterTactics.Options(FightHandler.BoardOf(fight), turret, spells);
            Assert.True(options.Where(o => o.Target == shorrokh).Max(o => o.Score)
                        <= options.Where(o => o.Target == near1 || o.Target == near2).Max(o => o.Score) + 0.001);
        }

        /// <summary>The rows that strip a shield are found from the spell's data, not from a monster id.</summary>
        [Fact]
        public void Sea_wave_s_952_on_state_56_is_a_stripping_row_and_a_blow_without_one_is_not()
        {
            if (SpellEffects.De(6851, 1).Count == 0) return;
            var rows = FightHandler.StrippingRows(6851, 1);
            Assert.Contains(rows, r => r.EffectId == 952 && r.Value == Invulnerable);
            Assert.Empty(FightHandler.StrippingRows(6855, 1));
        }

        [Fact]
        public async Task The_broken_turret_cannot_cast_its_sea_wave()
        {
            if (SpellEffects.De(6853, 1).Count == 0) return;
            var around = MapGeometry.GetNeighbors(300).ToList();
            var (fight, me, bag, turret, shorrokh) = Board(300, around[0], around[1], 450);
            await FightHandler.LanzarLaConductaAsync(null, fight, bag);
            Assert.False(SpellCriteria.Allows(turret, 6851, 1));
        }
    }
}
