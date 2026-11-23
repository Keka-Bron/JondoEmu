using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Ush Galesh (4264 red, 4265 black). The red one's behaviour, 6264, casts 6238 on him:
    /// he takes state 324, the black one 325, and 6239's 1061 shares every blow between the two.
    /// From his first turn end (6264 grade 2 on), an Ush in 324 only takes melee blows and one in
    /// 325 only ranged ones, and the blow that goes through flips him to the other state.
    /// 6238 needs a free cell at range 0, so it is never the AI's to cast by hand.
    /// </summary>
    [Collection("MapManager")]
    public class UshGaleshStatesTests
    {
        private const int RedUsh = 4264, BlackUsh = 4265, Behaviour = 6264, Twins = 6238,
                          HitInMelee = 324, HitAtRange = 325, SharesTheBlows = 1061, Blow = 2452;

        private sealed class Scene
        {
            public FightInstance Fight = null!;
            public Fighter Hitter = null!, Red = null!, Black = null!;
            public int Near, Far, FarFromBlack;
        }

        private static async Task<Scene?> Start()
        {
            MobSpawnManager.EnsureMonsterData();
            var red = MobSpawnManager.GetMonsterData(RedUsh);
            var black = MobSpawnManager.GetMonsterData(BlackUsh);
            if (red == null || black == null || SpellEffects.De(Twins, 1).Count == 0) return null;
            var s = new Scene { Fight = new FightInstance(9_934_264, 1) };
            s.Hitter = new Fighter { Id = 7413, TeamId = 0, CellId = 200, MaxHP = 5000, CurrentHP = 5000, Level = 200, Initiative = 1, MaxAP = 6, CurrentAP = 6 };
            s.Fight.AddPlayer(s.Hitter);
            s.Red = FightHandler.BuildMonsterFighter(new MobSpawnManager.MobMember { Monster = red, GradeIndex = 4 }, -1, 300);
            s.Black = FightHandler.BuildMonsterFighter(new MobSpawnManager.MobMember { Monster = black, GradeIndex = 4 }, -2, 330);
            s.Fight.AddMonster(s.Red);
            s.Fight.AddMonster(s.Black);
            s.Fight.StartFight();
            s.Red.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, s.Fight, s.Red);
            var fight = s.Fight;
            s.Near = MapGeometry.GetNeighbors(s.Red.CellId).First(c => fight.Todos.All(f => f.CellId != c));
            s.Far = Enumerable.Range(0, 560).First(c => MapGeometry.Distance(c, s.Red.CellId) is >= 4 and <= 6
                                                        && MapGeometry.Distance(c, s.Black.CellId) > 2 && fight.Todos.All(f => f.CellId != c));
            s.FarFromBlack = Enumerable.Range(0, 560).First(c => MapGeometry.Distance(c, s.Black.CellId) is >= 4 and <= 6
                                                                 && fight.Todos.All(f => f.CellId != c));
            return s;
        }

        private static string States(Fighter f) => $"{f.MonsterId}: [{string.Join(",", f.Buffs.Estados.OrderBy(x => x))}] hp {f.CurrentHP}/{f.MaxHP}";

        private static async Task Hit(Scene s, Fighter target, int from)
        {
            s.Hitter.CellId = from;
            await FightHandler.HurtAsync(null, s.Fight, s.Hitter, Blow, 1, target, target.CellId);
        }

        private static async Task NextRound(Scene s)
        {
            int r0 = s.Fight.RoundNumber;
            for (int t = 0; t < 10 && s.Fight.RoundNumber == r0; t++) s.Fight.NextTurn();
            foreach (var f in new[] { s.Red, s.Black })
            {
                await FightHandler.EngancheAsync(null, s.Fight, f, EffectEngine.AlEmpezarElTurno);
                await FightHandler.EngancheAsync(null, s.Fight, f, EffectEngine.AlAcabarElTurno);
            }
        }

        [Fact]
        public async Task The_AI_never_casts_the_twins_spell_by_hand()
        {
            var s = await Start();
            if (s == null) return;
            var fight = s.Fight;
            var board = new MonsterTactics.Board
            {
                Fighters = fight.Todos.ToArray(),
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
                CanSummon = (c, t, g) => FightHandler.FitsTheSummonLimit(fight, c, t, g),
            };
            var spells = FightHandler.TacticsOf(s.Red);
            Assert.Contains(spells, sp => sp.Id == Twins);
            var options = MonsterTactics.Options(board, s.Red, spells);
            Assert.DoesNotContain(options, o => o.Spell?.Id == Twins);
        }

        [Fact]
        public async Task The_blows_are_shared_in_every_round()
        {
            var s = await Start();
            if (s == null) return;
            var log = new StringBuilder("start: " + States(s.Red) + " | " + States(s.Black) + "\n");
            for (int round = 0; round < 4; round++)
            {
                await NextRound(s);
                Assert.Contains(s.Red.Buffs.Puestos, b => b.EffectId == SharesTheBlows);
                int black = s.Black.CurrentHP;
                await Hit(s, s.Red, s.Red.Buffs.TieneEstado(HitInMelee) ? s.Near : s.Far);
                log.AppendLine($"r{s.Fight.RoundNumber}: " + States(s.Red) + " | " + States(s.Black));
                Assert.True(s.Black.CurrentHP < black, log.ToString());
            }
        }

        [Fact]
        public async Task The_right_blow_flips_each_ush()
        {
            var s = await Start();
            if (s == null) return;
            Assert.True(s.Red.Buffs.TieneEstado(HitInMelee), States(s.Red));
            Assert.True(s.Black.Buffs.TieneEstado(HitAtRange), States(s.Black));
            await NextRound(s);
            Assert.True(s.Red.Buffs.TieneEstado(HitInMelee), States(s.Red));

            // A ranged blow on the red one in 324 does nothing...
            int hp = s.Red.CurrentHP;
            await Hit(s, s.Red, s.Far);
            Assert.Equal(hp, s.Red.CurrentHP);
            Assert.True(s.Red.Buffs.TieneEstado(HitInMelee), States(s.Red));
            // ...a melee one goes through and flips him to 325.
            await Hit(s, s.Red, s.Near);
            Assert.True(s.Red.CurrentHP < hp, States(s.Red));
            Assert.True(s.Red.Buffs.TieneEstado(HitAtRange) && !s.Red.Buffs.TieneEstado(HitInMelee), States(s.Red));

            // The black one in 325 is flipped by a ranged blow.
            await Hit(s, s.Black, s.FarFromBlack);
            Assert.True(s.Black.Buffs.TieneEstado(HitInMelee) && !s.Black.Buffs.TieneEstado(HitAtRange), States(s.Black));
        }
    }
}
