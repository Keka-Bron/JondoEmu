using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The sequence a cast goes in: a spell's is 0, a weapon's blow 1, as every cast of the captures
    /// opens -- «xelor-arma-garra de gargandias», frames 142-151: jto 1, jwe 303, jto 3, jxw, jwi 3,
    /// jwe 102, ..., jwi 1. In a 3, the type of what something sets off, the blow drew no slash.
    /// </summary>
    [Collection("MapManager")]
    public class CastSequenceTests
    {
        private static readonly ConcurrentDictionary<long, FightInstance> Active =
            (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        [Fact]
        public void A_spell_s_sequence_carries_no_type_and_a_weapon_s_carries_1()
        {
            // The capture's blow opens with f2 1; a spell's jto carries no f2 at all.
            Assert.Equal("082a1001", Hex(FightProtocol.BuildSequenceStart(42, FightProtocol.CastSequence(weapon: true))));
            Assert.Equal("082a", Hex(FightProtocol.BuildSequenceStart(42, FightProtocol.CastSequence(weapon: false))));
            Assert.Equal("0807102a", Hex(FightProtocol.BuildSequenceEnd(7, 42, FightProtocol.SpellSequence)));
            Assert.Equal("080a102a1801", Hex(FightProtocol.BuildSequenceEnd(10, 42, FightProtocol.WeaponSequence)));
        }

        [Fact]
        public async Task A_weapon_blow_opens_and_closes_a_weapon_sequence_around_a_set_off_one()
        {
            const long account = 8_997_000_011;
            const long map = 191105026;
            if (!MapManager.WalkableCells.ContainsKey(map)) MapManager.Initialize();
            long me = DatabaseManager.CreateCharacter(account, 1, "Secuencia-Uno", 5, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, map, 200, 0, 0, Array.Empty<(int, int)>());
            Assert.True(me > 0);
            try
            {
                var weapon = DatabaseManager.AddItemToInventory(me, 31786, 1);
                DatabaseManager.SaveItemPosition(weapon.Uid, 1, me);
                await using var pipe = await ClientPipe.OpenAsync(account, me, map, "Secuencia-Uno");
                pipe.Session.State.CharacterLevel = 200;
                pipe.Session.State.SetInventory(DatabaseManager.LoadInventory(me));

                var walk = MapManager.WalkableCells[map].OrderBy(c => c).ToList();
                int mine = walk[walk.Count / 2];
                int theirs = walk.Contains(mine + 14) ? mine + 14 : mine + 15;
                var fight = new FightInstance(me, map);
                fight.AddPlayer(new Fighter { Id = me, TeamId = 0, CellId = mine, MaxHP = 5000, CurrentHP = 5000, MaxAP = 12, CurrentAP = 12, Level = 200, Initiative = 9999 });
                fight.AddMonster(new Fighter { Id = -1, TeamId = 1, CellId = theirs, MaxHP = 4000, CurrentHP = 4000, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 31, Level = 100, Initiative = 1 });
                Active[fight.FightId] = fight;
                fight.StartFight();
                try
                {
                    using (SessionContext.Push(pipe.Session))
                        await FightHandler.CastAsync(pipe.ToClient, fight, theirs, 0);

                    var frames = new List<(string Op, byte[] Payload)>();
                    int depth = 0;
                    do
                    {
                        var (op, payload) = await pipe.NextAny();
                        frames.Add((op, payload));
                        if (op == Op.Jto) depth++;
                        if (op == Op.Jwi) depth--;
                    } while (depth > 0);

                    Assert.Equal(Op.Jto, frames[0].Op);
                    Assert.Equal(FightProtocol.WeaponSequence, Number(frames[0].Payload, 2));
                    Assert.Equal(Op.Jwe, frames[1].Op);
                    Assert.Equal(FightProtocol.WeaponCast, Number(frames[1].Payload, 14));
                    Assert.Equal(Op.Jto, frames[2].Op);
                    Assert.Equal(FightProtocol.ActionSequence, Number(frames[2].Payload, 2));
                }
                finally
                {
                    Active.TryRemove(fight.FightId, out _);
                }
            }
            finally
            {
                DatabaseManager.DeleteCharacter(me, account);
            }
        }

        private static long Number(byte[] payload, int field)
            => ProtoMessage.Parse(payload).Fields.FirstOrDefault(f => f.FieldNumber == field && f.WireType == 0)?.VarIntValue ?? 0;
    }
}
