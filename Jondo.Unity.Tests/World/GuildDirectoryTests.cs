using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The guild directory's list (jiv) as the 3.6.10.11 client reads it. fft.behg hands its f1s, f5,
    /// f6 and f3 to btj(list, f5, f6, f3); GuildDirectory.OnGuildList puts each guild at f5 + i, writes
    /// «f3/max Guildes» and sizes its list from f3 only when f6 is not zero. With the f1s alone, two
    /// guilds on the server showed «0/0 Guildes» and «Aucune guilde ne correspond à votre recherche».
    /// </summary>
    public class GuildDirectoryTests
    {
        private static GuildProtocol.DirectoryEntry Entry(long id, string name)
            => new GuildProtocol.DirectoryEntry { Guild = new GuildStore.Guild { Id = id, Name = name }, LeaderId = 7300 + id, Members = 1 };

        private static long Var(List<ProtoField> fields, int number)
            => (long)(fields.FirstOrDefault(f => f.FieldNumber == number && f.WireType == 0)?.VarIntValue ?? 0);

        [Fact]
        public void The_directory_says_how_many_guilds_it_lists_so_the_client_shows_them()
        {
            var jiv = ProtoMessage.Parse(GuildProtocol.BuildDirectory(new[] { Entry(1, "Dragon Ball Z"), Entry(2, "Spaghetih") })).Fields.ToList();

            Assert.Equal(2, jiv.Count(f => f.FieldNumber == 1 && f.WireType == 2));
            Assert.Equal(2, Var(jiv, 3));   // the guilds found: «2/2 Guildes»
            Assert.Equal(0, Var(jiv, 5));   // the page's first index
            Assert.Equal(2, Var(jiv, 6));   // not zero: the client sizes its list from f3
        }

        [Fact]
        public void An_empty_directory_stays_empty()
        {
            Assert.Empty(ProtoMessage.Parse(GuildProtocol.BuildDirectory(new GuildProtocol.DirectoryEntry[0])).Fields);
        }
    }
}
