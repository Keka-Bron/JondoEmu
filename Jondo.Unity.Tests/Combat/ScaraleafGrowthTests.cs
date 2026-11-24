using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The immature Scaraleaf (798) grows once: its behaviour 29833 is «792 29828 under TB,
    /// trigger duration 1», and 29828 gives state 5521 for two turns and, two turns later, +1 AP,
    /// +1 MP and +20% life. Hooked for good, 29828 went off at every turn start and each cast
    /// queued one more growth, so the AP and MP kept piling up every turn.
    /// </summary>
    [Collection("MapManager")]
    public class ScaraleafGrowthTests
    {
        private const int Behaviour = 29833;
        private const int Growth = 29828;
        private const int Growing = 5521;
        private const int ActionPoints = 1;
        private const int MovementPoints = 23;

        private static Fighter Player() => new()
        {
            Id = 9_970_601, Name = "P", CellId = 250, Level = 100, MaxHP = 5000, CurrentHP = 5000,
            MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3, Initiative = 5000,
        };

        private static Fighter Scaraleaf(long id, int cell) => new()
        {
            Id = id, Name = "Scaraleaf", IsMonster = true, CellId = cell, Level = 40, MaxHP = 250,
            CurrentHP = 250, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, MonsterId = 798,
            Initiative = 1000,
        };

        /// <summary>Plays the fight on to the given fighter's next turn start, as the fight does it.</summary>
        private static async Task ToTheTurnStartOf(FightInstance fight, Fighter who)
        {
            do
            {
                var next = fight.NextTurn();
                FightHandler.TurnStartSweep(fight, next);
                await FightHandler.ApplyDuePendingAsync(null, fight);
                await FightHandler.EngancheAsync(null, fight, next, EffectEngine.AlEmpezarElTurno);
            }
            while (fight.CurrentFighter != who);
        }

        [Fact]
        public async Task It_grows_once_and_the_growth_does_not_pile_up()
        {
            if (SpellEffects.De(Behaviour, 1).Count == 0 || SpellEffects.De(Growth, 1).Count == 0) return;
            var player = Player();
            var scaraleaf = Scaraleaf(-1, 300);
            var fight = new FightInstance(9_970_601, 990_029_833);
            fight.AddPlayer(player);
            fight.AddMonster(scaraleaf);
            player.CellId = 250; scaraleaf.CellId = 300;
            fight.StartFight();
            scaraleaf.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, scaraleaf);
            fight.CloseTheStart();

            int growths = 0;
            bool wasGrowing = false;
            for (int turn = 0; turn < 8; turn++)
            {
                await ToTheTurnStartOf(fight, scaraleaf);
                bool growing = scaraleaf.Buffs.TieneEstado(Growing);
                if (growing && !wasGrowing) growths++;
                wasGrowing = growing;
            }

            Assert.Equal(1, growths);
            Assert.Equal(1, scaraleaf.Buffs.De(ActionPoints, fight.RoundNumber));
            Assert.Equal(1, scaraleaf.Buffs.De(MovementPoints, fight.RoundNumber));
        }

        [Fact]
        public async Task Summoned_in_the_middle_of_a_round_it_grows_once_too()
        {
            if (SpellEffects.De(Behaviour, 2).Count == 0) return;
            var player = Player();
            var summoner = Scaraleaf(-1, 300);
            summoner.MonsterId = 797;
            var fight = new FightInstance(9_970_602, 990_029_834);
            fight.AddPlayer(player);
            fight.AddMonster(summoner);
            player.CellId = 250; summoner.CellId = 300;
            fight.StartFight();
            fight.CloseTheStart();

            // Two rounds in, the boss brings one out in his turn, as his Nacimiento does.
            await ToTheTurnStartOf(fight, summoner);
            await ToTheTurnStartOf(fight, summoner);
            var scaraleaf = Scaraleaf(-2, 302);
            fight.Invocar(scaraleaf, summoner);
            scaraleaf.Conducta = (Behaviour, 2);
            await FightHandler.LanzarLaConductaAsync(null, fight, scaraleaf);

            for (int turn = 0; turn < 8; turn++) await ToTheTurnStartOf(fight, summoner);

            Assert.Equal(1, scaraleaf.Buffs.De(ActionPoints, fight.RoundNumber));
            Assert.Equal(1, scaraleaf.Buffs.De(MovementPoints, fight.RoundNumber));
        }
    }
}
