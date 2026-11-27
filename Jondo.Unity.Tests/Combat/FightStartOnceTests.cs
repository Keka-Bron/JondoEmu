using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A fight's start with two people in it: each gets his own opening burst, but what happens on
    /// the board before the first turn -- the attitudes cast, the raids' glyphs, the turn asked for --
    /// happens once, after both bursts, so neither sees it twice nor before his own kai.
    /// </summary>
    [Collection("MapManager")]   // map 188744196: its fight notices reached FightJoinTests run in parallel
    public class FightStartOnceTests
    {
        /// <summary>An attitude that only puts a state on its caster (56), so casting it has nothing else to do.</summary>
        private const int Attitude = 4440;

        private static Fighter Person(long id, int cell)
        {
            var person = new Fighter
            {
                Id = id, Name = "P" + id, Level = 200, MaxHP = 1000, CurrentHP = 1000,
                MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, CellId = cell,
            };
            person.Buffs.PonerActitud(Attitude);
            return person;
        }

        [Fact]
        public async Task The_board_s_start_happens_once_after_everybody_s_burst()
        {
            const long first = 8_300_000_001, second = 8_300_000_002;
            const long map = 188744196;
            var fight = new FightInstance(9301, map, 191105032) { Reglas = FightRules.ContraMonstruos };
            fight.SetPlacementCells(new[] { 300, 301 }, new[] { 180, 181 });
            fight.AddPlayer(Person(first, 300));
            Assert.True(fight.JoinTeam(Person(second, 301), FightInstance.Azules, FightHandler.MaxPeoplePerTeam));
            fight.AddMonster(new Fighter { Id = -1, IsMonster = true, MonsterId = 492, MaxHP = 10, CurrentHP = 10 });
            fight.SetFighterReady(first);
            fight.SetFighterReady(second);

            var sent = new List<(long Recipient, GamePayload Message, int AttitudesApplied)>();
            var a = CreateClient(first);
            var b = CreateClient(second);
            Assert.True(SessionRegistry.Register(a));
            try
            {
                Assert.True(SessionRegistry.Register(b));
                await FightHandler.StartFightAsync(fight);

                var prompts = sent.Where(row => row.Message is FightIsTurnReadyEvent).ToArray();
                Assert.Equal(new[] { first, second }.Order(), prompts.Select(row => row.Recipient).Order());
                Assert.All(prompts, row => Assert.Equal(2, row.AttitudesApplied));
                Assert.Equal(prompts, sent.TakeLast(2));

                foreach (long id in new[] { first, second })
                {
                    var burst = sent.Where(row => row.Recipient == id).ToList();
                    int start = burst.FindIndex(row => row.Message is FightStartEvent);
                    int close = burst.FindIndex(row => row.Message is SequenceEndEvent);
                    Assert.True(start >= 0 && close > start);
                    Assert.Equal(0, burst[close].AttitudesApplied);
                    Assert.Single(fight.Buscar(id).Buffs.Puestos, buff => buff.Estado == 56);
                }
            }
            finally
            {
                SessionRegistry.Unregister(a);
                SessionRegistry.Unregister(b);
            }

            GameSession CreateClient(long id)
            {
                // Opening attitudes still send legacy action sequences. Observe their applied state instead.
                var session = new GameSession(new CombatRecordingCodec(frame => sent.Add((id, frame.Content,
                    fight.Azul.Sum(fighter => fighter.Buffs.Puestos.Count(buff => buff.Estado == 56))))),
                    _ => Task.CompletedTask);
                session.BindAccount(id + 1, 1);
                session.State.CharacterId = id;
                session.State.MapId = map;
                session.EnterWorld();
                return session;
            }
        }
    }
}
