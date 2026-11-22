using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
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

        /// <summary>Every frame that arrives until the line goes quiet.</summary>
        private static async Task<List<string>> DrainAsync(ClientPipe pipe)
        {
            var opcodes = new List<string>();
            while (true)
            {
                var read = Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer);
                if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(1.5))) != read) return opcodes;
                byte[] frame = await read;
                if (frame == null) return opcodes;
                opcodes.Add(ConnectionProtocol.ReadOpcode(frame) ?? "");
            }
        }

        [Fact]
        public async Task The_board_s_start_happens_once_after_everybody_s_burst()
        {
            const long first = 8_300_000_001, second = 8_300_000_002;
            const long map = 188744196;
            await using var a = await ClientPipe.OpenAsync(8301, first, map, "A");
            await using var b = await ClientPipe.OpenAsync(8302, second, map, "B");
            var fight = new FightInstance(9301, map, 191105032) { Reglas = FightRules.ContraMonstruos };
            fight.SetPlacementCells(new[] { 300, 301 }, new[] { 180, 181 });
            fight.AddPlayer(Person(first, 300));
            Assert.True(fight.JoinTeam(Person(second, 301), FightInstance.Azules, FightHandler.MaxPeoplePerTeam));
            fight.AddMonster(new Fighter { Id = -1, IsMonster = true, MonsterId = 492, MaxHP = 10, CurrentHP = 10 });
            fight.SetFighterReady(first);
            fight.SetFighterReady(second);

            await FightHandler.StartFightAsync(fight);

            foreach (var opcodes in new[] { await DrainAsync(a), await DrainAsync(b) })
            {
                int kai = opcodes.IndexOf(Op.Kai);
                Assert.True(kai >= 0, "no kai: " + string.Join(",", opcodes));

                // The opening's own sequence, and one for each attitude: two people, one each.
                Assert.Equal(3, opcodes.Count(o => o == Op.Jto));
                Assert.True(opcodes.IndexOf(Op.Jto) > kai, "a sequence came before the kai: " + string.Join(",", opcodes));

                // And the first turn asked for once, last.
                Assert.Equal(1, opcodes.Count(o => o == Op.Jxh));
                Assert.True(opcodes.LastIndexOf(Op.Jto) < opcodes.IndexOf(Op.Jxh));
            }
            Assert.Single(fight.Buscar(first).Buffs.Puestos, p => p.Estado == 56);
        }
    }
}
