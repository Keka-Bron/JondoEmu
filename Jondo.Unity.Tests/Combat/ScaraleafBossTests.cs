using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Scaraleaf boss (797) in his own room: Nacimiento (790) brings out an immature Scaraleaf
    /// one to three cells away, and Niebla Envenenada (789) takes AP and MP from everybody round him.
    /// </summary>
    [Collection("MapManager")]
    public class ScaraleafBossTests
    {
        private const long BossRoom = 94115846L;
        private const int Nacimiento = 790;
        private const int PoisonedFog = 789;
        private const int BossCell = 231, PlayerCell = 217;

        private static (FightInstance Fight, Fighter Boss)? Room(params (int Cell, int Monster)[] others)
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.WalkableCells.ContainsKey(BossRoom)) return null;
            var fight = new FightInstance(9_970_790, BossRoom, BossRoom);
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = BossCell, IsMonster = true, MonsterId = 797, Level = 40,
                MaxHP = 1500, CurrentHP = 1500, MaxAP = 8, CurrentAP = 8, MaxMP = 3, CurrentMP = 3, Agility = 280,
            };
            foreach (int spell in new[] { 788, 29830, 29829, PoisonedFog, Nacimiento })
            {
                boss.SpellIds.Add(spell);
                boss.SpellGrades[spell] = 1;
            }
            var player = new Fighter
            {
                Id = 9_970_790, TeamId = 0, CellId = PlayerCell, Level = 50, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            fight.AddPlayer(player);
            fight.AddOpponent(boss);
            long id = -2;
            foreach (var (cell, monster) in others)
                fight.AddOpponent(new Fighter
                {
                    Id = id--, CellId = cell, IsMonster = true, MonsterId = monster, Level = 40, MaxHP = 300,
                    CurrentHP = 300, MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3,
                });
            return (fight, boss);
        }

        private static System.Collections.Generic.List<MonsterTactics.Spell> Usable(Fighter boss)
            => FightHandler.TacticsOf(boss)
                .Where(s => SpellCriteria.Allows(boss, s.Id, s.Grade) && !(boss.Recarga.TryGetValue(s.Id, out int w) && w > 0))
                .ToList();

        [Fact]
        public void Hemmed_in_he_brings_his_scaraleaf_out_farther_off()
        {
            // The player beside him, a Scaraleaf on each of his other free neighbours, and the
            // bushes on the rest: none of the cells next to him is free.
            var room = Room((216, 795), (245, 798));
            if (room == null) return;
            var (fight, boss) = room.Value;
            var board = FightHandler.BoardOf(fight);
            if (!FightHandler.TacticsOf(boss).Any(s => s.Id == Nacimiento)) return;
            Assert.DoesNotContain(MapGeometry.GetNeighbors(BossCell),
                c => board.Walkable(c) && !board.Occupied(c));

            var births = MonsterTactics.Options(board, boss, Usable(boss)).Where(o => o.Spell.Id == Nacimiento).ToList();

            Assert.NotEmpty(births);
            Assert.All(births, birth =>
            {
                int distance = MapGeometry.Distance(BossCell, birth.TargetCell);
                Assert.InRange(distance, 1, 3);
                Assert.True(board.Walkable(birth.TargetCell) && !board.Occupied(birth.TargetCell));
                Assert.True(distance <= 1 || board.Sees(BossCell, birth.TargetCell));
            });
        }

        [Fact]
        public void With_his_summon_waiting_he_fogs_the_player_beside_him()
        {
            var room = Room();
            if (room == null) return;
            var (fight, boss) = room.Value;
            if (!FightHandler.TacticsOf(boss).Any(s => s.Id == PoisonedFog)) return;
            boss.Recarga[Nacimiento] = 4;

            var next = MonsterTactics.Next(FightHandler.BoardOf(fight), boss, Usable(boss));

            Assert.NotNull(next);
            Assert.Equal(PoisonedFog, next!.Spell.Id);
        }
    }
}
