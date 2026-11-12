using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Social
{
    /// <summary>
    /// A monster group shared in the chat goes as «{monsterGroup,id}»: the line carries what the
    /// group is (kti f8, ktp f2, one kte per group), from which the client draws «Leader [x,y]».
    /// Without it the line showed the bare «{monsterGroup,-1021015}».
    /// </summary>
    public class ChatLinksTests
    {
        [Fact]
        public void A_shared_group_carries_its_leader_its_id_and_where_it_is()
        {
            var group = new MobSpawnManager.MobGroup
            {
                MobId = -1021015,
                Members =
                {
                    new MobSpawnManager.MobMember { Monster = new MobSpawnManager.MonsterData { Id = 3271 } },
                    new MobSpawnManager.MobMember { Monster = new MobSpawnManager.MonsterData { Id = 3270 } },
                },
            };
            var map = new MapInfo { MapId = 1, PosX = -2, PosY = 5 };

            var kte = ProtoMessage.Parse(ChatLinks.MonsterGroupEntry(group, map).Build());

            Assert.Equal(3271, kte.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(-1021015, kte.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(1, kte.Fields.Single(f => f.FieldNumber == 4).VarIntValue);
            var at = ProtoMessage.Parse(kte.Fields.Single(f => f.FieldNumber == 5).BytesValue);
            Assert.Equal(-2, (int)at.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(5, (int)at.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.False(string.IsNullOrEmpty(System.Text.Encoding.UTF8.GetString(kte.Fields.Single(f => f.FieldNumber == 7).BytesValue)));
        }

        /// <summary>
        /// Its tooltip's text, as the client makes it: «grade x monster x level» for each, by «|» --
        /// taken in the running client from three Chakrobats and two Ronronchons.
        /// </summary>
        [Fact]
        public void The_group_s_infos_are_the_client_s_own_text()
        {
            MobSpawnManager.MobMember Member(int monster, int grade, int level)
                => new() { Monster = new MobSpawnManager.MonsterData { Id = monster }, GradeIndex = grade - 1, Level = level };
            var group = new MobSpawnManager.MobGroup
            {
                MobId = -1,
                Members = { Member(982, 3, 6), Member(982, 3, 6), Member(982, 2, 5), Member(4105, 1, 6), Member(4105, 1, 6) },
            };

            Assert.Equal("3x982x6|3x982x6|2x982x5|1x4105x6|1x4105x6", ChatLinks.GroupInfos(group));
        }

        [Fact]
        public void A_line_without_a_group_carries_nothing()
        {
            Assert.Null(ChatLinks.Of("bonjour", 1));
            Assert.Null(ChatLinks.Of("", 1));
        }

        [Fact]
        public void The_chat_line_carries_the_links_in_its_f8()
        {
            var links = Pb.New().Msg(2, Pb.New().Var(2, 7));
            var kti = ProtoMessage.Parse(ConnectionProtocol.BuildChatLine("Sley", 1, 2, "{monsterGroup,7}", 0, links));
            Assert.Equal(links.Build(), kti.Fields.Single(f => f.FieldNumber == 8).BytesValue);

            var plain = ProtoMessage.Parse(ConnectionProtocol.BuildChatLine("Sley", 1, 2, "salut", 0));
            Assert.Empty(plain.Fields.Single(f => f.FieldNumber == 8).BytesValue);
        }
    }
}
