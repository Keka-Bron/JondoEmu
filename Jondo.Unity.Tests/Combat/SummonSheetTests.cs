using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// What a summon is when it comes out and what goes with it when it dies, against the sheets
    /// and the deaths of the captures.
    /// </summary>
    [Collection("koliseo")]
    public class SummonSheetTests
    {
        /// <summary>
        /// The jwe 181 of the captures, summoners of level 200: the grade's characteristics times
        /// three, the bonus as it is, and three fifths of the bonus damage as power.
        /// </summary>
        [Theory]
        [InlineData(300, 0, 900)]    // Aniripsa's 7370, Hipermago's 5129, the Ocra's 2630
        [InlineData(220, 0, 660)]    // 246, 262
        [InlineData(400, 0, 1200)]   // the Sacrógrito's sword 434
        [InlineData(135, 0, 405)]    // 5845
        [InlineData(0, 50, 50)]      // the Osamodas' Tofu 8070: agility in the bonus only
        [InlineData(0, 100, 100)]    // 8078
        public void A_summons_characteristic_is_the_captures(int own, int bonus, int onTheSheet)
        {
            Assert.Equal(onTheSheet, Summons.CaracteristicaDelInvocado(own, bonus, 200));
        }

        [Theory]
        [InlineData(50, 30)]
        [InlineData(75, 45)]
        [InlineData(100, 60)]
        public void An_animals_power_is_three_fifths_of_its_bonus_damage(int bonusDamage, int power)
        {
            Assert.Equal(power, Summons.PotenciaDelInvocado(bonusDamage));
        }

        /// <summary>The Tofu of a level-200 Osamodas: 50 of agility and 30 of power, read off its template.</summary>
        [Fact]
        public void The_tofu_comes_out_with_its_agility_and_its_power()
        {
            var tofu = Summons.De(8070, 3);
            Assert.NotNull(tofu);
            Assert.Equal(50, Summons.CaracteristicaDelInvocado(tofu!.Agilidad, tofu.BonusAgilidad, 200));
            Assert.Equal(30, Summons.PotenciaDelInvocado(tofu.BonusDeDanos));
            Assert.Equal(525, Summons.VidaDelInvocado(tofu.Vida, 200, tofu.VidaFija));
        }

        /// <summary>
        /// "explobomba-...-explotandolas", frames 3799-3819: each bomb dies and the real server
        /// takes its Encendimiento -- "+1 AP to Explobomba" on the Tymador -- off him. Kept, the
        /// cost of every bomb of the fight added up and a second bomb could not be paid for.
        /// </summary>
        [Fact]
        public async Task A_bombs_cost_on_its_owner_goes_with_the_bomb()
        {
            const long tymador = 8_950_000_301;
            await using var wire = await PortalTests.Wire.Open(tymador);
            var fight = new FightInstance(8_950_301, 1, 1);
            var owner = new Fighter { Id = tymador, MaxHP = 3000, CurrentHP = 3000, Level = 200 };
            var enemy = new Fighter { Id = -99, MaxHP = 3000, CurrentHP = 3000, Level = 200, IsMonster = true };
            fight.AddPlayer(owner);
            fight.AddOpponent(enemy);
            var bomb = new Fighter { Id = -2, TeamId = owner.TeamId, Invocador = owner.Id, IsMonster = true, MonsterId = 3112, MaxHP = 945, CurrentHP = 945 };
            fight.Invocar(bomb, owner);

            int number = 0;
            var cost = owner.Buffs.Poner(new Buff { EffectId = 296, Quien = bomb.Id, HechizoOrigen = 13444, CaducaEnRonda = -1 }, () => ++number);

            bomb.CurrentHP = 0;
            await FightHandler.CaenSusInvocadosAsync(wire.Session.Stream!, fight, bomb);

            Assert.DoesNotContain(owner.Buffs.Puestos, b => b.Quien == bomb.Id);
            Assert.Contains(await wire.Drain(), f => f.Op == Op.Jya);
        }

        /// <summary>A player does not meet the same class again until eight others have come.</summary>
        [Fact]
        public void A_player_meets_every_class_before_meeting_one_again()
        {
            const long player = 8_950_000_302;
            var seen = new System.Collections.Generic.List<int>();
            try
            {
                for (int i = 0; i <= KoliseoBots.RecentClasses; i++)
                {
                    var spec = KoliseoBots.Create(against: player);
                    seen.Add(spec.Breed);
                    KoliseoBots.Forget(spec.Id);
                }
                Assert.Equal(seen.Count, seen.Distinct().Count());
            }
            finally { }
        }
    }
}
