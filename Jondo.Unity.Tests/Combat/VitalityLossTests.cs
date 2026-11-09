using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Vitality moves the maximum life and the life a fighter has, both ways: the client counts
    /// life as the maximum less what was lost, so a fighter under Vitalidad is drawn with the
    /// bonus in his life too, and loses it again when the row goes -- whichever way it goes. A
    /// Yopuka at 100 of life who gets 1,000 of vitality, takes a blow of 150 and is then stripped
    /// of the bonus is left at -50: dead.
    /// </summary>
    public class VitalityLossTests
    {
        private const int Vitalidad = 13120, PalaFantomatica = 13336;

        private static (FightInstance Fight, Fighter Yopuka, Fighter Foe) Board(int life)
        {
            var fight = new FightInstance(1, 1);
            var yopuka = new Fighter { Id = 1, TeamId = 0, CellId = 300, MaxHP = 5000, CurrentHP = life, Level = 200,
                                       MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8 };
            var foe = new Fighter { Id = 2, TeamId = 1, CellId = 304, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                                    MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 3 };
            fight.AddPlayer(yopuka); fight.AddOpponent(foe);
            return (fight, yopuka, foe);
        }

        private static async Task Blow(FightInstance fight, Fighter from, Fighter to, int amount)
        {
            var blow = new SpellEffect { EffectId = 100, EffectUid = 990001, DiceNum = amount, DiceSide = 0, TargetMask = "A" };
            await FightHandler.HurtAsync(null, fight, from, 1, 1, to, to.CellId, tirada: new[] { blow });
        }

        /// <summary>A spell cast as a player casts it: its boosts, its blows, then the rest.</summary>
        private static Task Cast(FightInstance fight, Fighter caster, int spell, int grade, Fighter target)
            => FightHandler.CastEffectsAsync(null, fight, caster, spell, grade, target, target.CellId, false,
                                             EffectEngine.EfectosSorteados(spell, grade, false));

        [Fact]
        public async Task Vitalidad_raises_the_life_he_has_with_the_maximum()
        {
            var (fight, yopuka, _) = Board(100);
            await FightHandler.AplicarEfectosAsync(null, fight, yopuka, Vitalidad, 3, yopuka, EffectEngine.AlLanzar, yopuka.CellId);

            Assert.Equal(6000, yopuka.MaxHP);
            Assert.Equal(1100, yopuka.CurrentHP);
        }

        [Fact]
        public async Task Stripped_of_it_below_what_it_gave_he_dies()
        {
            var (fight, yopuka, foe) = Board(100);
            await FightHandler.AplicarEfectosAsync(null, fight, yopuka, Vitalidad, 3, yopuka, EffectEngine.AlLanzar, yopuka.CellId);
            await Blow(fight, foe, yopuka, 150);
            Assert.True(yopuka.IsAlive);
            int hit = 1100 - yopuka.CurrentHP;
            Assert.True(hit >= 150, $"the blow took {hit}");

            // One turn of it left, and Pala Fantomática takes one away: it goes, and its 1,000 with it.
            foreach (var row in yopuka.Buffs.Puestos.Where(Fighter.IsVitalityRow)) row.CaducaEnRonda = fight.RoundNumber + 1;
            await Cast(fight, foe, PalaFantomatica, 3, yopuka);

            Assert.DoesNotContain(yopuka.Buffs.Puestos, Fighter.IsVitalityRow);
            Assert.False(yopuka.IsAlive, $"left at {yopuka.CurrentHP}/{yopuka.MaxHP}");
            Assert.Equal(5000, yopuka.MaxHP);
        }

        [Fact]
        public async Task Stripped_of_it_with_life_to_spare_he_keeps_the_rest()
        {
            var (fight, yopuka, foe) = Board(2000);
            await FightHandler.AplicarEfectosAsync(null, fight, yopuka, Vitalidad, 3, yopuka, EffectEngine.AlLanzar, yopuka.CellId);
            Assert.Equal(3000, yopuka.CurrentHP);
            await Blow(fight, foe, yopuka, 150);
            int left = yopuka.CurrentHP;

            foreach (var row in yopuka.Buffs.Puestos.Where(Fighter.IsVitalityRow)) row.CaducaEnRonda = fight.RoundNumber + 1;
            await Cast(fight, foe, PalaFantomatica, 3, yopuka);

            Assert.True(yopuka.IsAlive);
            Assert.Equal(5000, yopuka.MaxHP);
            // The 1,000 of the bonus and the pala's own blow.
            Assert.True(yopuka.CurrentHP <= left - 1000, $"{left} before, {yopuka.CurrentHP} after");
        }

        [Fact]
        public void A_vitality_row_running_out_takes_its_life_back_once()
        {
            var fighter = new Fighter { Id = 1, MaxHP = 5000, CurrentHP = 5000 };
            var row = new Buff { EffectId = Jondo.Unity.World.Combat.EffectSupport.VitalityFlatBonus, Caracteristica = 11, Cuanto = 400,
                                 Disparador = "I", CaducaEnRonda = 3, EmpiezaEnRonda = 1 };
            int n = 0;
            fighter.Buffs.Poner(row, () => ++n);
            Assert.Equal((5400, 5400), (fighter.MaxHP, fighter.CurrentHP));

            fighter.Buffs.Barrer(3, _ => true).ToList();
            Assert.Equal((5000, 5000), (fighter.MaxHP, fighter.CurrentHP));
        }
    }
}
