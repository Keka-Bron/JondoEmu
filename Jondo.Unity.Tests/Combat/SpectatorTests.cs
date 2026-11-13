using System;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Server;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Watching a fight: who may, the spectator's way in (kab) and the spectator window's list of the
    /// map's fights (jyc), as the client reads them -- fff.bdzg and fjq.bfuk for the kab, gop.blai
    /// for the list.
    /// </summary>
    public class SpectatorTests
    {
        private const long Map = 191105026;

        private static FightInstance Running()
        {
            var fight = new FightInstance(77, Map) { RoleplayMapId = Map };
            fight.AddPlayer(new Fighter { Id = 9_100_001, TeamId = 0, CellId = 200, MaxHP = 500, CurrentHP = 500, Level = 50, Name = "Uno" });
            fight.AddMonster(new Fighter { Id = -1, TeamId = 1, CellId = 300, MaxHP = 100, CurrentHP = 100, IsMonster = true,
                                           MonsterId = 494, Level = 10 });
            return fight;
        }

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        [Fact]
        public void Only_a_running_fight_open_to_spectators_is_watched()
        {
            var fight = Running();
            var who = new SessionState { CharacterId = 9_100_002, MapId = Map };

            Assert.Equal("no such fight", FightHandler.WhyNotWatch(null, who, fromTheMap: true));
            Assert.Equal("the fight has not started", FightHandler.WhyNotWatch(fight, who, fromTheMap: true));

            fight.StartFight();
            Assert.Null(FightHandler.WhyNotWatch(fight, who, fromTheMap: true));

            var elsewhere = new SessionState { CharacterId = 9_100_003, MapId = Map + 1 };
            Assert.Equal("not on the map of the fight", FightHandler.WhyNotWatch(fight, elsewhere, fromTheMap: true));
            Assert.Null(FightHandler.WhyNotWatch(fight, elsewhere, fromTheMap: false));   // a friend's fight, from afar

            var watching = new SessionState { CharacterId = 9_100_004, MapId = Map, SpectatingFightId = 12 };
            Assert.Equal("already in a fight", FightHandler.WhyNotWatch(fight, watching, fromTheMap: true));

            fight.SetOption(FightInstance.Azules, FightInstance.OptionSecret, true);
            Assert.Equal("the fight takes no spectators", FightHandler.WhyNotWatch(fight, who, fromTheMap: true));
        }

        /// <summary>kab { f3 kaa }: the kaa of a fight in progress, without the ready flags.</summary>
        [Fact]
        public void The_spectator_join_carries_a_running_fight_s_kaa()
        {
            Assert.Equal("1a0408013004", Hex(FightProtocol.BuildSpectatorJoined(4)));
            Assert.Equal("1a020801", Hex(FightProtocol.BuildSpectatorJoined(0)));
        }

        /// <summary>
        /// The list: jwr { f2 jwv { f1 the fight, f3 an ISO start, f5 each team { f1 lgw with its f1
        /// case { f3 how many, f8 mean level }, f2 the options } } }.
        /// </summary>
        [Fact]
        public void The_map_s_fights_list_as_the_client_reads_it()
        {
            var team = FightJoinProtocol.LightTeam(0, 9_100_001, 0, 2, 75);
            var fight = FightJoinProtocol.RunningFight(77, 4, new DateTime(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc), false,
                                                       new[] { (team, true) });
            var jyc = ProtoMessage.Parse(FightJoinProtocol.BuildRunningFights(new[] { fight }));
            var jwr = ProtoMessage.Parse(jyc.Fields.Single(f => f.FieldNumber == 1).BytesValue);
            var jwv = ProtoMessage.Parse(jwr.Fields.Single(f => f.FieldNumber == 2).BytesValue);
            Assert.Equal(77, jwv.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal("2026-10-22T09:30:00Z", System.Text.Encoding.UTF8.GetString(jwv.Fields.Single(f => f.FieldNumber == 3).BytesValue));
            Assert.DoesNotContain(jwv.Fields, f => f.FieldNumber == 4);

            var jwt = ProtoMessage.Parse(jwv.Fields.Single(f => f.FieldNumber == 5).BytesValue);
            var lgw = ProtoMessage.Parse(jwt.Fields.Single(f => f.FieldNumber == 1).BytesValue);
            var lcu = ProtoMessage.Parse(lgw.Fields.Single(f => f.FieldNumber == 1).BytesValue);
            Assert.Equal(2, lcu.Fields.Single(f => f.FieldNumber == 3).VarIntValue);
            Assert.Equal(75, lcu.Fields.Single(f => f.FieldNumber == 8).VarIntValue);
            Assert.Equal(9_100_001, lgw.Fields.Single(f => f.FieldNumber == 3).VarIntValue);
            Assert.DoesNotContain(lgw.Fields, f => f.FieldNumber == 5);   // the other case of the oneof
            Assert.Equal("2001", Hex(jwt.Fields.Single(f => f.FieldNumber == 2).BytesValue));   // party only: f4
        }

        /// <summary>
        /// A fight's people (jqy): the defenders in f2 and the attackers in f3; a person by his
        /// name (f6), a monster by itself (f7), a tax collector by his two names (f2).
        /// </summary>
        [Fact]
        public void A_fight_s_details_name_people_monsters_and_tax_collectors()
        {
            var jqy = ProtoMessage.Parse(FightJoinProtocol.BuildFightDetails(77,
                new[] { FightJoinProtocol.FighterInDetails(9_100_001, 50, 0, "Uno", breed: 8, female: true) },
                new[] { FightJoinProtocol.FighterInDetails(-1, 10, 494, ""),
                        FightJoinProtocol.FighterInDetails(-2, 60, 0, "", 12, 34) }));
            Assert.Equal(77, jqy.Fields.Single(f => f.FieldNumber == 1).VarIntValue);

            var person = ProtoMessage.Parse(jqy.Fields.Single(f => f.FieldNumber == 3).BytesValue);
            Assert.Equal("Uno", System.Text.Encoding.UTF8.GetString(
                ProtoMessage.Parse(person.Fields.Single(f => f.FieldNumber == 6).BytesValue).Fields.Single().BytesValue));
            // Its head: the class in f5, the sex in f3.
            Assert.Equal(8, person.Fields.Single(f => f.FieldNumber == 5).VarIntValue);
            Assert.Equal(1, person.Fields.Single(f => f.FieldNumber == 3).VarIntValue);

            var defenders = jqy.Fields.Where(f => f.FieldNumber == 2).Select(f => ProtoMessage.Parse(f.BytesValue)).ToList();
            Assert.Equal(494, ProtoMessage.Parse(defenders[0].Fields.Single(f => f.FieldNumber == 7).BytesValue)
                                  .Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.DoesNotContain(defenders[0].Fields, f => f.FieldNumber == 2);
            // A monster's head is drawn from f7 only when f5 says -2.
            Assert.Equal(FightJoinProtocol.MonsterHead, (int)defenders[0].Fields.Single(f => f.FieldNumber == 5).VarIntValue);
            var names = ProtoMessage.Parse(defenders[1].Fields.Single(f => f.FieldNumber == 2).BytesValue).Fields;
            Assert.Equal(12, names.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(34, names.Single(f => f.FieldNumber == 2).VarIntValue);
        }
    }
}
