using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Two neutral hits that dealt nothing though the client previewed them: the bare-handed
    /// punch (spell 0, 6-7 neutral) and the admins' Pichenette (30092, 1 fixed neutral, 144).
    /// </summary>
    [Collection("MapManager")]
    public class NeutralHitTests
    {
        private const long Wielder = 9_970_402;

        private static (FightInstance Fight, Fighter Caster, Fighter Monster) Board()
        {
            var caster = new Fighter
            {
                Id = Wielder, Name = "W", CellId = 300, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Strength = 100,
            };
            var monster = new Fighter
            {
                Id = -1, Name = "M", IsMonster = true, CellId = 301, Level = 50, MaxHP = 50000, CurrentHP = 40000,
                MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, MonsterId = 494,
            };
            var fight = new FightInstance(4108, 990_004_108);
            fight.AddPlayer(caster); caster.CellId = 300;
            fight.AddMonster(monster); monster.CellId = 301;
            return (fight, caster, monster);
        }

        [Fact]
        public async Task A_bare_handed_punch_hurts()
        {
            var hand = FightHandler.ArmaEnLaMano;
            var session = GameSession.SinSocket();
            session.State.CharacterId = Wielder;
            try
            {
                FightHandler.ArmaEnLaMano = _ => null;
                using (SessionContext.Push(session))
                {
                    var (fight, caster, monster) = Board();
                    int before = monster.CurrentHP;

                    await FightHandler.HurtAsync(null, fight, caster, 0, 0, monster, 301);

                    Assert.True(monster.CurrentHP < before, "the punch took no life");
                }
            }
            finally
            {
                FightHandler.ArmaEnLaMano = hand;
            }
        }

        [Fact]
        public void Neutral_grows_with_strength_not_the_highest_characteristic()
        {
            var steamer = new Fighter { Strength = 105, Intelligence = 510, Chance = 1283, Agility = 105 };

            Assert.Equal(105, steamer.GetStatForElement(ElementType.Neutral));
        }

        [Fact]
        public async Task A_punch_grows_with_strength()
        {
            var hand = FightHandler.ArmaEnLaMano;
            var session = GameSession.SinSocket();
            session.State.CharacterId = Wielder;
            try
            {
                FightHandler.ArmaEnLaMano = _ => null;
                using (SessionContext.Push(session))
                {
                    var (fight, caster, monster) = Board();
                    caster.Chance = 1283;
                    int before = monster.CurrentHP;

                    await FightHandler.HurtAsync(null, fight, caster, 0, 0, monster, 301);

                    // 6-7 at 100 strength is 12-14; with the chance it came out at 80-97.
                    Assert.InRange(before - monster.CurrentHP, 12, 14);
                }
            }
            finally
            {
                FightHandler.ArmaEnLaMano = hand;
            }
        }

        [Fact]
        public void The_fixed_neutral_damage_is_a_fixed_blow()
        {
            Assert.True(EffectEngine.EsDeDano(144));
            Assert.Equal(EffectEngine.ModoDeDano.Fijo, EffectEngine.ModoDe(144));
        }

        [Fact]
        public async Task Pichenette_takes_one_life_point()
        {
            var (fight, caster, monster) = Board();
            int before = monster.CurrentHP;

            await FightHandler.HurtAsync(null, fight, caster, 30092, 1, monster, 301);

            Assert.Equal(before - 1, monster.CurrentHP);
        }
    }
}
