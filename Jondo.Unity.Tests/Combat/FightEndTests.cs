using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// How a fight ends: asked for with a jxh and shown once the client has answered it, as every
    /// fight end of the captures does.
    /// </summary>
    public class FightEndTests
    {
        private const long Player = 8_950_000_101;

        /// <summary>A player against a JondoBot, in the JondoBot's turn.</summary>
        private static FightInstance AgainstABot(out Fighter human, out Fighter bot)
        {
            var fight = new FightInstance(8_950_101, 1, 1);
            human = new Fighter { Id = Player, MaxHP = 1000, CurrentHP = 1000, Level = 200, Initiative = 1 };
            bot = new Fighter
            {
                Id = KoliseoBots.FirstId, IsBot = true, MaxHP = KoliseoBots.Life, CurrentHP = KoliseoBots.Life,
                Level = KoliseoBots.Level, Initiative = 5000,
            };
            fight.AddPlayer(human);
            fight.AddOpponent(bot);
            fight.StartFight();
            return fight;
        }

        /// <summary>
        /// The Dopeul's defeat (Movimiento/submarino steamer-...-perder, frames 2474-2491): the
        /// monster's killing sequence, a jxh naming it, the client's jwz 6.5 s later -- the time
        /// its blows take to show -- and only then the kuf and the jyg. The end does not go out
        /// behind the blow: it waits for the jwz.
        /// </summary>
        [Fact]
        public async Task A_fight_lost_in_the_bots_turn_waits_for_the_client_to_play_it()
        {
            await using var wire = await PortalTests.Wire.Open(Player);
            var fight = AgainstABot(out var human, out var bot);
            Assert.Same(bot, fight.CurrentFighter);
            human.CurrentHP = 0;

            Assert.True(await FightHandler.CheckFightOverAsync(null!, fight));

            var (op, payload) = Assert.Single(await wire.Drain());
            Assert.Equal(Op.Jxh, op);
            Assert.Equal(FightProtocol.BuildConfirmTurn(bot.Id), payload);
            Assert.Equal(FightInstance.WaitsForTheJwz, fight.FinPendiente);
            Assert.Equal(FightState.Ongoing, fight.State);
        }

        [Fact]
        public async Task A_fight_with_both_sides_standing_asks_nothing()
        {
            await using var wire = await PortalTests.Wire.Open(Player);
            var fight = AgainstABot(out _, out _);

            Assert.False(await FightHandler.CheckFightOverAsync(null!, fight));

            Assert.Empty(await wire.Drain());
            Assert.Equal(0, fight.FinPendiente);
        }
    }
}
