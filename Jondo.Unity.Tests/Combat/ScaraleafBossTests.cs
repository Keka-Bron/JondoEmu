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
        private const int Scaraleaf = 795, ImmatureScaraleaf = 798;

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
                    // An immature Scaraleaf in his room is one he brought out.
                    Invocador = monster == ImmatureScaraleaf ? boss.Id : 0,
                });
            // His turn, as the fight weighs it: the one playing is the only fighter that does not
            // stand in the line of sight (FightHandler.BlocksSight). Weighed on the player's turn,
            // the player beside him hid nothing and the boss "saw" past him.
            fight.StartFight();
            for (int turn = 0; turn < 10 && fight.CurrentFighter != boss; turn++) fight.NextTurn();
            Assert.Same(boss, fight.CurrentFighter);
            return (fight, boss);
        }

        private static System.Collections.Generic.List<MonsterTactics.Spell> Usable(Fighter boss)
            => FightHandler.TacticsOf(boss)
                .Where(s => SpellCriteria.Allows(boss, s.Id, s.Grade) && !(boss.Recarga.TryGetValue(s.Id, out int w) && w > 0))
                .ToList();

        /// <summary>
        /// Round 6 of the measured fight: the player on 217, Scaraleafs on 216, 232 and 203, his
        /// own immature Scaraleaf on 245 and the hole on 244. Nacimiento is cast in line and in
        /// sight (CastInLine, CastTestLos), and every cell on his four lines within three is taken,
        /// not walkable or behind one of those fighters: there is nowhere to cast it, and he did
        /// not until a line opened (round 14, on 217). His summon being alive has nothing to do
        /// with it -- he has no summon cap (summon_caps.json) and his +5 lasts one round.
        /// </summary>
        [Fact]
        public void With_every_line_shut_there_is_nowhere_to_bring_one_out()
        {
            var room = Room((216, Scaraleaf), (245, ImmatureScaraleaf), (232, Scaraleaf), (203, Scaraleaf));
            if (room == null) return;
            var (fight, boss) = room.Value;
            var board = FightHandler.BoardOf(fight);
            var birth = Usable(boss).FirstOrDefault(s => s.Id == Nacimiento);
            if (birth == null) return;
            Assert.True(FightHandler.FitsTheSummonLimit(fight, boss, ImmatureScaraleaf, 1));

            var castable = Enumerable.Range(0, MapGeometry.MaxCells)
                .Where(c => { int d = MapGeometry.Distance(BossCell, c); return d >= 1 && d <= birth.ReachFor(boss); })
                .Where(c => MonsterTactics.OnItsLines(birth, BossCell, c))
                .Where(c => board.Walkable(c) && !board.Occupied(c))
                .Where(c => MapGeometry.Distance(BossCell, c) <= 1 || board.Sees(BossCell, c));
            Assert.Empty(castable);
            Assert.DoesNotContain(MonsterTactics.Options(board, boss, Usable(boss)), o => o.Spell.Id == Nacimiento);
        }

        /// <summary>
        /// The same room with the Scaraleaf on 216 walked off: a line is open, and with his first
        /// immature Scaraleaf still alive he brings out another, on that line.
        /// </summary>
        [Fact]
        public void With_his_scaraleaf_alive_he_brings_another_once_a_line_opens()
        {
            var room = Room((245, ImmatureScaraleaf), (232, Scaraleaf), (203, Scaraleaf));
            if (room == null) return;
            var (fight, boss) = room.Value;
            var board = FightHandler.BoardOf(fight);
            var birth = Usable(boss).FirstOrDefault(s => s.Id == Nacimiento);
            if (birth == null) return;
            Assert.Equal(1, FightHandler.UsedSummonCapacity(fight, boss));
            boss.Recarga[PoisonedFog] = 2;

            var next = MonsterTactics.Next(board, boss, Usable(boss));

            Assert.NotNull(next);
            Assert.Equal(Nacimiento, next!.Spell.Id);
            Assert.True(MonsterTactics.OnItsLines(birth, BossCell, next.TargetCell));
            Assert.True(board.Walkable(next.TargetCell) && !board.Occupied(next.TargetCell));
            Assert.True(MapGeometry.Distance(BossCell, next.TargetCell) <= 1 || board.Sees(BossCell, next.TargetCell));
        }

        /// <summary>
        /// Rounds 6 to 13 of a later fight: Scaraleafs (194, 241) on 232 and 203, his immature
        /// Scaraleaf on 245, and a Scaraleaf (198) that walks to 202 to summon on its turn and,
        /// its casts done, walks back beside the player on 216 (a melee monster ends next to the
        /// weakest enemy). Left on 202 it would open 216 and the boss would bring out another
        /// there; back on 216 it shuts his last line before his turn comes, so he holds
        /// Nacimiento -- the rule of the spell (in line, in sight, a free cell) and not a fault
        /// of his summon count or of the interval.
        /// </summary>
        [Fact]
        public void The_scaraleaf_beside_him_walks_back_and_shuts_his_last_line()
        {
            var room = Room((232, 194), (203, 241), (202, 198), (245, ImmatureScaraleaf));
            if (room == null) return;
            var (fight, boss) = room.Value;
            var birth = Usable(boss).FirstOrDefault(s => s.Id == Nacimiento);
            var grade = DatabaseManager.GetMonsterGradeStats(198, 1);
            if (birth == null || grade == null || grade.SpellIds.Count == 0) return;
            var walker = fight.Rojo.Concat(fight.Azul).First(f => f.MonsterId == 198);
            foreach (int spell in grade.SpellIds)
            {
                walker.SpellIds.Add(spell);
                walker.SpellGrades[spell] = 1;
            }
            boss.Recarga[PoisonedFog] = 2;

            // While it stands on 202, 216 is open and the boss brings one out there.
            var open = MonsterTactics.Next(FightHandler.BoardOf(fight), boss, Usable(boss));
            Assert.NotNull(open);
            Assert.Equal(Nacimiento, open!.Spell.Id);
            Assert.Equal(216, open.TargetCell);

            // Its turn over, it walks back beside the player.
            var path = MonsterTactics.Reposition(FightHandler.BoardOf(fight), walker, FightHandler.TacticsOf(walker));
            Assert.Equal(216, path.Last());
            walker.CellId = path.Last();

            Assert.DoesNotContain(MonsterTactics.Options(FightHandler.BoardOf(fight), boss, Usable(boss)),
                                  o => o.Spell.Id == Nacimiento);
        }

        /// <summary>
        /// Hemmed in on every side, the boss looks further off for a cell to bring his Scaraleaf
        /// out on. Nacimiento is cast in line: a cell off his lines is refused by the fight
        /// (FightHandler's CastInLine check), so it is never the one aimed at, even where it is
        /// free and in sight.
        /// </summary>
        [Fact]
        public void Hemmed_in_he_aims_his_summon_only_along_his_lines()
        {
            var room = Room((216, Scaraleaf), (245, ImmatureScaraleaf));
            if (room == null) return;
            var (fight, boss) = room.Value;
            var board = FightHandler.BoardOf(fight);
            var birth = Usable(boss).FirstOrDefault(s => s.Id == Nacimiento);
            if (birth == null) return;
            Assert.DoesNotContain(MapGeometry.GetNeighbors(BossCell), c => board.Walkable(c) && !board.Occupied(c));
            bool Open(int c)
            {
                int d = MapGeometry.Distance(BossCell, c);
                return d >= 2 && d <= birth.ReachFor(boss) && board.Walkable(c) && !board.Occupied(c) && board.Sees(BossCell, c);
            }
            // Cells he could reach but not on his lines exist here: the test has something to refuse.
            Assert.Contains(Enumerable.Range(0, MapGeometry.MaxCells), c => Open(c) && !MonsterTactics.OnItsLines(birth, BossCell, c));

            var births = MonsterTactics.Options(board, boss, Usable(boss)).Where(o => o.Spell.Id == Nacimiento).ToList();

            Assert.All(births, o => Assert.True(MonsterTactics.OnItsLines(birth, o.From, o.TargetCell)));
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
