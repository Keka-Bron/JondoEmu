using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Steamer's Turbine on an empty cell: its level wants an occupied one, the client offers
    /// the empty ones, and the server refused them -- the spell did nothing. Aimed at nobody it
    /// pulls and evolves without its cross of fire.
    /// </summary>
    [Collection("MapManager")]
    public class TurbineTests
    {
        private const int Turbina = 13874, Steamer = 15;

        private static readonly ConcurrentDictionary<long, FightInstance> Active =
            (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        [Fact]
        public async Task On_an_empty_cell_it_is_cast_without_its_fire_and_on_somebody_with_it()
        {
            const long account = 8_997_000_021;
            const long map = 191105026;
            if (!MapManager.WalkableCells.ContainsKey(map)) MapManager.Initialize();
            if (!MapManager.WalkableCells.TryGetValue(map, out var walkable)) return;   // no world data
            long me = DatabaseManager.CreateCharacter(account, Steamer, "Turbina-Uno", 5, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, map, 200, 0, 0, Array.Empty<(int, int)>());
            Assert.True(me > 0);

            var walk = walkable.OrderBy(c => c).ToList();
            int mine = walk[walk.Count / 2];
            // An empty cell two away, and a monster next to it.
            int empty = walk.First(c => MapGeometry.Distance(mine, c) == 2);
            int theirs = walk.First(c => c != mine && MapGeometry.Distance(empty, c) == 1 && MapGeometry.Distance(mine, c) == 3);

            await using var pipe = await ClientPipe.OpenAsync(account, me, map, "Turbina-Uno");
            pipe.Session.State.CharacterLevel = 200;
            pipe.Session.State.Breed = Steamer;
            var fight = new FightInstance(me, map);
            var steamer = new Fighter { Id = me, TeamId = 0, CellId = mine, MaxHP = 5000, CurrentHP = 5000, MaxAP = 12, CurrentAP = 12, Level = 200, Initiative = 9999, Breed = Steamer };
            var mob = new Fighter { Id = -1, TeamId = 1, CellId = theirs, MaxHP = 4000, CurrentHP = 4000, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 31, Level = 100, Initiative = 1 };
            fight.AddPlayer(steamer);
            fight.AddMonster(mob);
            steamer.CellId = mine;
            mob.CellId = theirs;
            Active[fight.FightId] = fight;
            fight.StartFight();
            try
            {
                using (SessionContext.Push(pipe.Session))
                {
                    await FightHandler.CastAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jwh, Pb.New().Var(1, empty).Var(4, Turbina).Build()));
                    Assert.True(steamer.CurrentAP < 12, $"not cast on the empty cell {empty}");
                    Assert.Equal(4000, mob.CurrentHP);

                    int ap = steamer.CurrentAP;
                    await FightHandler.CastAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jwh, Pb.New().Var(1, mob.CellId).Var(4, Turbina).Build()));
                    Assert.True(steamer.CurrentAP < ap, $"not cast on the monster's cell {mob.CellId}");
                    Assert.True(mob.CurrentHP < 4000, "no fire on the monster aimed at");
                }
            }
            finally
            {
                Active.TryRemove(fight.FightId, out _);
            }
        }
    }
}
