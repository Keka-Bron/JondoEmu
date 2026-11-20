using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Tynril's Laboratory (dungeon 51): its four Tynrils, through the real data.
    /// </summary>
    /// <remarks>
    /// Dofus pour les noobs: each one resists 200 % in every element but its own; each turn it
    /// throws -2000 % of its element's resistance on the nearest character, «lorsque deux Tynrils
    /// sont au CàC l'un de l'autre, ils peuvent se reconstituer», and «à chaque tour d'un Tynril,
    /// celui-ci se transpose avec un autre afin de lui prendre sa place». The four come alone.
    /// </remarks>
    [Collection("MapManager")]
    public class TynrilTests
    {
        private const long BossRoom = 89392130;
        private const int Perfido = 1086, Atonito = 1087, Absorto = 1085, Estupefacto = 1072;
        private const int Forca = 1060, Besillon = 1054;

        private static Fighter Tynril(long id, int template, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 5400, CurrentHP = 5400, Level = 140, IsMonster = true,
            MonsterId = template, MaxAP = 10, CurrentAP = 10, MaxMP = 2, CurrentMP = 2,
        };

        /// <summary>
        /// Forca swaps the Tynril with another of his side («g», one to sixty-three cells): it is
        /// aimed at one of them, never at himself, and is worth casting.
        /// </summary>
        [Fact]
        public void Forca_is_aimed_at_another_tynril()
        {
            if (SpellEffects.De(Forca, 1).Count == 0) return;   // no data
            var fight = new FightInstance(1, 1);
            var perfido = Tynril(-1, Perfido, 300);
            var atonito = Tynril(-2, Atonito, 200);
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 450, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
            fight.AddPlayer(player); fight.AddMonster(perfido); fight.AddMonster(atonito);
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { perfido, atonito, player },
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
            };

            var forca = Assert.Single(FightHandler.TacticsFor(perfido, new[] { (Forca, 1) }));
            Assert.NotEmpty(forca.UtilityOwnSideMasks);
            Assert.True(forca.UtilityReachesOwnSide(perfido, atonito));
            Assert.False(forca.UtilityReachesOwnSide(perfido, perfido));

            var options = MonsterTactics.Options(board, perfido, new[] { forca });
            Assert.Contains(options, o => o.Spell.Id == Forca && o.Target == atonito && o.Score > 0);
            Assert.DoesNotContain(options, o => o.Spell.Id == Forca && o.Target != atonito);
        }

        /// <summary>
        /// Besillón gives life back to a Tynril beside him who has lost some: two of them in melee
        /// put each other back together.
        /// </summary>
        [Fact]
        public void A_tynril_heals_a_wounded_one_beside_him()
        {
            if (SpellEffects.De(Besillon, 1).Count == 0) return;   // no data
            var fight = new FightInstance(1, 1);
            var perfido = Tynril(-1, Perfido, 300);
            var (x, y) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(300);
            var absorto = Tynril(-2, Absorto, Jondo.Unity.World.Maps.MapGeometry.PointToCell(x + 1, y));
            absorto.CurrentHP = 2000;
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 450, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
            fight.AddPlayer(player); fight.AddMonster(perfido); fight.AddMonster(absorto);
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { perfido, absorto, player },
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
            };

            var heal = Assert.Single(FightHandler.TacticsFor(perfido, new[] { (Besillon, 1) }));
            var options = MonsterTactics.Options(board, perfido, new[] { heal });
            Assert.Contains(options, o => o.Spell.Id == Besillon && o.Target == absorto && o.Score > 0);
        }

        /// <summary>The four Tynrils come alone, whoever fights them: none of the dungeon's other monsters.</summary>
        [Fact]
        public void The_four_tynrils_are_fought_alone()
        {
            Assert.Contains(51, DungeonManager.BossFightsAlone);
            DungeonManager.Initialize();
            if (MobSpawnManager.GetMonsterData(Perfido) == null) MobSpawnManager.InitializeAndSpawnAll();
            if (MobSpawnManager.GetMonsterData(Perfido) == null) return;   // no world data
            Assert.NotNull(MobSpawnManager.RecomposeDungeonRoom(BossRoom));
            var groups = MobSpawnManager.GetMobsForMap(BossRoom);
            if (groups.Count == 0) return;   // no world data

            var group = Assert.Single(groups);
            Assert.Equal(new[] { Estupefacto, Absorto, Perfido, Atonito }.OrderBy(m => m),
                         group.Members.Select(m => m.Monster!.Id).OrderBy(m => m));
            Assert.Equal(4, MobSpawnManager.MembersFor(group, 1).Count);
            Assert.Equal(4, MobSpawnManager.MembersFor(group, 8).Count);
        }
    }
}
