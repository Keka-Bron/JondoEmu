using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
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
            using var client = new Client();
            var fight = AgainstABot(out var human, out var bot);
            Assert.Same(bot, fight.CurrentFighter);
            human.CurrentHP = 0;

            Assert.True(await FightHandler.CheckFightOverAsync(null!, fight));

            client.AssertSinglePrompt();
            Assert.Equal(FightInstance.WaitsForTheJwz, fight.FinPendiente);
            Assert.Equal(FightState.Ongoing, fight.State);
        }

        /// <summary>
        /// The player had died and the ally who had to live kept the side standing; when the ally
        /// fell too, the side that "falls at once" had nobody left in it, and the fight waited
        /// for an end that never came. It ends the usual way: a jxh, then the client's jwz.
        /// </summary>
        [Fact]
        public async Task The_ally_who_had_to_live_falling_last_still_ends_the_fight()
        {
            using var client = new Client();
            var fight = AgainstABot(out var human, out _);
            var ally = new Fighter { Id = -1, IsMonster = true, MaxHP = 500, CurrentHP = 500, Level = 50 };
            fight.AddPlayer(ally);
            fight.AlliesMustLive = true;
            human.CurrentHP = 0;
            ally.CurrentHP = 0;
            fight.WonByEffect = FightInstance.Rojos;

            Assert.True(await FightHandler.EffectEndedTheFightAsync(null!, fight));

            client.AssertSinglePrompt();
            Assert.Equal(FightInstance.WaitsForTheJwz, fight.FinPendiente);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Defeat_sequence_sends_just_one_prompt_and_retains_the_closing_action(bool surrender)
        {
            using var client = new Client();
            var fight = AgainstABot(out var human, out _);
            int priorAction = fight.SiguienteAccion();
            if (surrender)
            {
                var active = (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                    .GetField("_activeFights", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                Assert.True(active.TryAdd(fight.FightId, fight));
                try
                {
                    client.Session.State.FightId = fight.FightId;
                    client.Session.State.IsInFight = true;
                    using var turn = await client.Session.EnterTurnAsync();
                    using var context = SessionContext.Push(client.Session);
                    await FightHandler.QuitAsync(client.Session);
                }
                finally { active.TryRemove(fight.FightId, out _); }
            }
            else
            {
                fight.WonByEffect = FightInstance.Rojos;
                Assert.True(await FightHandler.EffectEndedTheFightAsync(null!, fight));
            }

            Assert.False(human.IsAlive);
            Assert.Collection(client.Frames,
                f => Assert.Equal(new SequenceStartEvent(SequenceType.CharacterDeath), f.Content),
                f => Assert.Equal(new GameActionFightEvent(human.Id, FightProtocol.Died, new FightActionCaseDeath(new(human.Id))), f.Content),
                f => Assert.IsType<FightTurnListEvent>(f.Content),
                f => Assert.Equal(priorAction + 1, Assert.IsType<SequenceEndEvent>(f.Content).ActionId),
                f => Assert.IsType<FightIsTurnReadyEvent>(f.Content));
            Assert.All(client.Frames, f => Assert.Null(f.Uid));
            Assert.Equal(priorAction + 1, fight.FinPendiente);
            Assert.Equal(FightState.Ongoing, fight.State);
        }

        [Fact]
        public async Task A_fight_with_both_sides_standing_asks_nothing()
        {
            using var client = new Client();
            var fight = AgainstABot(out _, out _);

            Assert.False(await FightHandler.CheckFightOverAsync(null!, fight));

            Assert.Empty(client.Frames);
            Assert.Equal(0, fight.FinPendiente);
        }

        private sealed class Client : IDisposable
        {
            internal List<GameFrame> Frames { get; } = [];
            internal GameSession Session { get; }

            internal Client()
            {
                Session = new(new CombatRecordingCodec(Frames.Add), CombatRecordingCodec.RequireTypedPacketAsync);
                Session.BindAccount(Player + 1, 1);
                Session.State.CharacterId = Player;
                Session.State.MapId = 1;
                Session.EnterWorld();
                Assert.True(SessionRegistry.Register(Session));
            }

            internal void AssertSinglePrompt()
            {
                var frame = Assert.Single(Frames);
                Assert.IsType<FightIsTurnReadyEvent>(frame.Content);
                Assert.Null(frame.Uid);
            }

            public void Dispose() => SessionRegistry.Unregister(Session);
        }
    }
}
