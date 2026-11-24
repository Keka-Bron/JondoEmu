using System;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Chafer Ronin's Bunshin (3165): its «Crea ilusiones» (1024, four of them) makes copies
    /// of the monster, as Tymadura's 1097 makes copies of a player. Before, 1024 was read by no
    /// branch of the engine: the boss passed his turn and nothing appeared.
    /// </summary>
    public class MonsterIllusionTests
    {
        private const int Bunshin = 3165;
        private const int ChaferRonin = 3238;
        private const long Hero = 53000000031;

        private static (FightInstance Fight, Fighter Boss) Board()
        {
            var fight = new FightInstance(1, 1);
            var hero = new Fighter { Id = Hero, TeamId = 0, CellId = 400, MaxHP = 900, CurrentHP = 900, Level = 40 };
            fight.AddPlayer(hero);
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 300, MaxHP = 600, CurrentHP = 450, Level = 40,
                IsMonster = true, MonsterId = ChaferRonin, GradeIndex = 3, Name = "Chafer Ronin",
            };
            fight.AddOpponent(boss);
            return (fight, boss);
        }

        private static Outcome Cast(FightInstance fight, Fighter boss, int aimed)
            => EffectEngine.Resolver(fight, boss, Bunshin, 1, boss, EffectEngine.AlLanzar,
                                     fight.RoundNumber, celdaApuntada: aimed)
                           .SingleOrDefault(o => o.Ilusiones != null);

        [Fact]
        public void Bunshin_makes_four_copies_of_the_monster_and_the_boss_jumps()
        {
            var (fight, boss) = Board();

            var made = Cast(fight, boss, 302);
            Assert.NotNull(made);

            Assert.Equal(302, boss.CellId);
            Assert.Equal(300, made.CasillaDesde);
            Assert.Equal(302, made.CasillaHasta);
            Assert.Equal(4, made.Ilusiones.Count);
            Assert.Equal(4, made.Ilusiones.Select(i => i.CellId).Distinct().Count());
            Assert.DoesNotContain(made.Ilusiones, i => i.CellId == boss.CellId);
            Assert.True(boss.IllusionsOutlastHisTurn);
            Assert.All(made.Ilusiones, i =>
            {
                Assert.True(i.EsIlusion);
                Assert.True(i.IsMonster);
                Assert.Equal(ChaferRonin, i.MonsterId);
                Assert.Equal(boss.GradeIndex, i.GradeIndex);
                Assert.Equal(boss.TeamId, i.TeamId);
                Assert.Equal(boss.MaxHP, i.MaxHP);
                Assert.Equal(boss.CurrentHP, i.CurrentHP);
                Assert.False(i.JuegaTurno);
                Assert.DoesNotContain(i, fight.TurnOrder);
                Assert.Contains(i.Id, boss.Ilusiones);
            });
        }

        [Fact]
        public void A_second_bunshin_only_makes_up_the_copies_that_were_lost()
        {
            var (fight, boss) = Board();
            var first = Cast(fight, boss, 302);
            Assert.NotNull(first);

            var lost = first.Ilusiones[0];
            lost.CurrentHP = 0;
            fight.Quitar(lost);

            var second = Cast(fight, boss, 304);
            Assert.NotNull(second);
            Assert.Single(second.Ilusiones);
            Assert.Equal(4, boss.Ilusiones.Count(id => fight.Buscar(id)?.IsAlive == true));
        }

        [Fact]
        public void Both_illusion_effects_create_illusions()
        {
            Assert.True(EffectSupport.CreatesIllusions(EffectSupport.Illusions));
            Assert.True(EffectSupport.CreatesIllusions(EffectSupport.MonsterIllusions));
            Assert.False(EffectSupport.CreatesIllusions(EffectSupport.IllusionGone));
        }

        [Fact]
        public void A_monster_copy_is_announced_with_the_monsters_identity()
        {
            var sheet = FightProtocol.IllusionSheet(40);
            byte[] look = { 0x0a, 0x02, 0x08, 0x01 };
            var identity = FightProtocol.MonsterIdentity(4, ChaferRonin, 40);

            byte[] plain = FightProtocol.BuildIllusion(-1, -8, 302, 1, 300, 3, sheet, look, team: 1);
            byte[] monster = FightProtocol.BuildIllusion(-1, -8, 302, 1, 300, 3, sheet, look, team: 1,
                                                         monsterIdentity: identity);

            byte[] wanted = identity.Build();
            Assert.True(monster.Length > plain.Length);
            Assert.True(Contains(monster, wanted));
            Assert.False(Contains(plain, wanted));
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
            return false;
        }
    }
}
