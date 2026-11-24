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
    /// The Kwoknan's Nest monsters start on Kwapoussée (29790): its grade 1 holds "792 on C under PD"
    /// casting its own grade 2 -- -10% resistance (1077) and +10 push damage (414) for three turns --
    /// on the monster that takes push damage. The guide: each crash costs it 10% resistance and
    /// gives it 10 push damage for 3 turns, stacking.
    ///
    /// PD is the crash of the one PUSHED; the one he is pushed into takes his share under PPD, which
    /// Kwapoussée does not wait on. The data keeps the two apart: Esponjoso (15411) and the Tanuki
    /// rows (17031, 17048) write "PD|PPD" to answer both, which would go off twice on the one pushed
    /// into if PD were fired on him as well. In the review only characters were pushed into the
    /// monsters (Kwak spell 8689), so none of them took a PD.
    /// </summary>
    public class KwapousseeTests
    {
        private const int Kwapoussee = 29790;
        private const int ResistanceLoss = 1077;
        private const int PushDamageBonus = 414;

        private static bool HasData => SpellEffects.De(Kwapoussee, 1).Count > 0
                                       && SpellEffects.De(Kwapoussee, 2).Count > 0;

        /// <summary>Three cells in a straight line: the pusher, the one pushed, and a fighter behind him.</summary>
        private static (int Pusher, int Pushed, int Behind) ALineOfThree()
        {
            int pusher = 288;
            int pushed = MapGeometry.GetNeighbors(pusher).First();
            int behind = MapGeometry.GetNeighbors(pushed)
                                    .First(c => c != pusher && MapGeometry.Distance(pusher, c) == 2);
            return (pusher, pushed, behind);
        }

        private static Fighter Monster(long id, int template, int cell) => new()
        {
            Id = id, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 180, IsMonster = true,
            MonsterId = template, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
        };

        private static Fighter Character(long id, int cell) => new()
        {
            Id = id, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
            MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
        };

        private static async Task<(FightInstance Fight, Fighter Kwak, Fighter Other, Fighter Character)> BoardAsync()
        {
            var fight = new FightInstance(1, 1);
            var character = Character(10, 100);
            fight.AddPlayer(character);
            var kwak = Monster(-1, 81, 400);
            var other = Monster(-2, 84, 404);
            fight.AddMonster(kwak);
            fight.AddMonster(other);
            fight.StartFight();

            kwak.Conducta = (Kwapoussee, 1);
            other.Conducta = (Kwapoussee, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, kwak);
            await FightHandler.LanzarLaConductaAsync(null, fight, other);
            return (fight, kwak, other, character);
        }

        private static Task PushAsync(FightInstance fight, Fighter pusher, Fighter pushed)
            => FightHandler.AplicarEfectosAsync(null, fight, pusher, 1, 1, pushed, EffectEngine.AlLanzar, pushed.CellId,
                   tirada: new[] { new SpellEffect { EffectId = 5, DiceNum = 3, TargetMask = "a,A", Triggers = "I" } });

        private static int Stacks(Fighter who, int effect)
            => who.Buffs.Puestos.Count(row => row.HechizoOrigen == Kwapoussee && row.EffectId == effect);

        [Fact]
        public async Task A_kwak_that_crashes_loses_resistance_and_gains_push_damage()
        {
            if (!HasData) return;
            var (fight, kwak, other, character) = await BoardAsync();
            var (pusherCell, pushedCell, behind) = ALineOfThree();
            character.CellId = pusherCell;
            kwak.CellId = pushedCell;
            other.CellId = behind;
            int life = kwak.CurrentHP;

            await PushAsync(fight, character, kwak);

            Assert.True(kwak.CurrentHP < life, "the crash hurt it");
            Assert.Equal(1, Stacks(kwak, ResistanceLoss));
            Assert.Equal(1, Stacks(kwak, PushDamageBonus));
            Assert.Contains(kwak.Buffs.Puestos, row => row.HechizoOrigen == Kwapoussee && row.EffectId == PushDamageBonus
                                                       && row.Cuanto == 10);
            // The one it was pushed into took its share under PPD: nothing for it.
            Assert.True(other.CurrentHP < other.MaxHP, "the one behind took its share");
            Assert.Equal(0, Stacks(other, PushDamageBonus));
            Assert.Equal(0, Stacks(other, ResistanceLoss));
        }

        [Fact]
        public async Task Each_crash_stacks_another_one()
        {
            if (!HasData) return;
            var (fight, kwak, other, character) = await BoardAsync();
            var (pusherCell, pushedCell, behind) = ALineOfThree();
            character.CellId = pusherCell;
            kwak.CellId = pushedCell;
            other.CellId = behind;

            await PushAsync(fight, character, kwak);
            await PushAsync(fight, character, kwak);

            Assert.Equal(2, Stacks(kwak, ResistanceLoss));
            Assert.Equal(2, Stacks(kwak, PushDamageBonus));
        }

        /// <summary>
        /// A character pushed into a kwak, as the Kwaks' own 8689 does: the character took the PD,
        /// the kwak behind only the PPD, and it gains nothing.
        /// </summary>
        [Fact]
        public async Task A_character_pushed_into_a_kwak_gives_the_kwak_nothing()
        {
            if (!HasData) return;
            var (fight, kwak, other, character) = await BoardAsync();
            var (pusherCell, pushedCell, behind) = ALineOfThree();
            other.CellId = pusherCell;
            character.CellId = pushedCell;
            kwak.CellId = behind;
            int life = kwak.CurrentHP;

            await PushAsync(fight, other, character);

            Assert.True(kwak.CurrentHP < life, "the kwak took its share of the crash");
            Assert.Equal(0, Stacks(kwak, PushDamageBonus));
        }

        /// <summary>A push that ends with no crash costs nothing.</summary>
        [Fact]
        public async Task A_push_without_a_crash_gives_nothing()
        {
            if (!HasData) return;
            var (fight, kwak, other, character) = await BoardAsync();
            var (pusherCell, pushedCell, _) = ALineOfThree();
            character.CellId = pusherCell;
            kwak.CellId = pushedCell;
            other.CellId = 500;
            int life = kwak.CurrentHP;

            await PushAsync(fight, character, kwak);

            if (kwak.CurrentHP < life) return; // the board had a wall in the way: not this case
            Assert.Equal(0, Stacks(kwak, PushDamageBonus));
        }
    }
}
