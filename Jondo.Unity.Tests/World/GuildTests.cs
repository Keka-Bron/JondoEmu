using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The guild, against the capture of creating «Jondo»: the three frames the real server sends
    /// on founding -the guild you belong to, its ranks and its header- byte for byte, and the
    /// store that feeds them.
    /// </summary>
    /// <remarks>
    /// In the same collection as the raids: both classes point the store at a pass-through base
    /// with the SAME static switch, so running at once they step on each other and one finds
    /// the other's base. A shared collection is what tells xUnit not to overlap them.
    /// </remarks>
    [Collection("guild raids")]
    public class GuildTests
    {
        /// <summary>The capture's guild: emblem 165/8/16744448/9476018, id 42043, «Jondo», level 1.</summary>
        private static GuildStore.Guild Jondo() => new()
        {
            Id = 42043,
            Name = "Jondo",
            Level = 1,
            EmblemSymbol = 165,
            EmblemSymbolColor = 8,
            EmblemBackground = 16744448,
            EmblemSymbolRgb = 9476018,
            FoundedUtc = "2026-08-12T20:53:02.187515342Z",
        };

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        [Theory]
        [InlineData("Jondo")]
        [InlineData("Les BG")]
        [InlineData("L'été-2")]
        [InlineData("ABC")]
        public void Guild_names_accepted_by_the_command_are_valid(string name)
        {
            Assert.True(GuildHandler.IsValidGuildName(name));
            Assert.Equal(1575, GuildHandler.GuildalogemTemplate);
        }

        [Theory]
        [InlineData("")]
        [InlineData("AB")]
        [InlineData("Nom!")]
        [InlineData("1234567890123456789012345678901")]
        public void Guild_names_rejected_by_the_command_are_invalid(string name)
        {
            Assert.False(GuildHandler.IsValidGuildName(name));
        }

        /// <summary>
        /// «You belong to this guild» (jgw) just as it came out on creating «Jondo»: position 1 and the
        /// guild block with its emblem.
        /// </summary>
        [Fact]
        public void The_guild_you_belong_to_is_the_capture()
        {
            Assert.Equal("10011a200a111a0f08a5011008188080fe0728b2afc20410bbc8021a054a6f6e646f2001",
                         Hex(GuildProtocol.BuildGuildJoined(Jondo(), rank: 1)));
        }

        /// <summary>
        /// The window header (jhh) of a freshly created guild: the founding date, the
        /// level 1, the 50 members that fit and the only one there is.
        /// </summary>
        [Fact]
        public void The_guild_header_is_the_capture()
        {
            Assert.Equal("0a1e323032362d30382d31325432303a35333a30322e3138373531353334325a180148325001",
                         Hex(GuildProtocol.BuildGuildInfo(Jondo(), memberCount: 1)));
            Assert.Equal(50, GuildStore.MaxMembers(1));
        }

        /// <summary>The four default ranks (jco), the whole mould of a new guild.</summary>
        [Fact]
        public void The_default_ranks_are_the_capture()
        {
            Assert.Equal("123912116775696c642e72616e6b2e312e6e616d651a1e1a1c0102050607080d0e0f1718191a1d1e1f" +
                         "202122232425262728292a2b220210742801123212116775696c642e72616e6b2e322e6e616d65" +
                         "1a1508011a11010205062607270828290d0e0f1718191a2204107318012802121f12116775696c" +
                         "642e72616e6b2e332e6e616d651a0208012204107218022803121d12116775696c642e72616e6b" +
                         "2e342e6e616d651a002204107518032804",
                         Hex(GuildProtocol.BuildDefaultRanks()));
        }

        /// <summary>
        /// The guild seen from the map (jhe) carries the same block as the jgw, the position and the
        /// constant ending that appears in all the «Jondo» captures.
        /// </summary>
        [Fact]
        public void The_guild_of_an_actor_carries_the_same_block()
        {
            byte[] jhe = GuildProtocol.BuildActorGuild(Jondo(), rank: 1, memberExperience: 10);
            byte[] jgw = GuildProtocol.BuildGuildJoined(Jondo(), rank: 1);

            // The jgw's guild block goes in its f3 and the jhe's in its f1: the same bytes.
            string bloque = "0a111a0f08a5011008188080fe0728b2afc20410bbc8021a054a6f6e646f2001";
            Assert.Contains(bloque, Hex(jhe));
            Assert.Contains(bloque, Hex(jgw));
            Assert.Equal("0a200a111a0f08a5011008188080fe0728b2afc20410bbc8021a054a6f6e646f2001" +
                         "1001180a20ff0d", Hex(jhe));
        }

        /// <summary>
        /// «You are invited to a guild» (jiq), byte for byte against the capture of receiving an
        /// invitation to the guild «Hezbola» from «Harmoo».
        /// </summary>
        [Fact]
        public void The_invitation_is_the_capture()
        {
            var hezbola = new GuildStore.Guild
            {
                Id = 6846, Name = "Hezbola", Level = 5,
                EmblemSymbol = 334, EmblemSymbolColor = 34,
                EmblemBackground = 16511237, EmblemSymbolRgb = 2140438,
            };

            Assert.Equal("0a210a111a0f08ce0210221885e2ef072896d2820110be351a0748657a626f6c61200512064861726d6f6f",
                         Hex(GuildProtocol.BuildInvitation(hezbola, "Harmoo")));
        }

        /// <summary>
        /// The shop (jkh) of a four-account guild: the five oracles at 80, 80, 800, 200
        /// and 80, which is each one's price times the four. Byte for byte.
        /// </summary>
        [Fact]
        public void The_shop_is_the_capture()
        {
            Assert.Equal("12400804120a08011206085012021a00120a08021206085012021a00120b0803120708a00612021a00" +
                         "120b0804120708c80112021a00120a08051206085012021a00",
                         Hex(GuildProtocol.BuildShop(accounts: 4)));
        }

        /// <summary>
        /// And that of the single-account guild, which is where the bare prices are read: 20, 20,
        /// 200, 50 and 20.
        /// </summary>
        [Fact]
        public void The_shop_of_a_one_account_guild_carries_the_bare_prices()
        {
            Assert.Equal("123f0801120a08011206081412021a00120a08021206081412021a00120b0803120708c8011" +
                         "2021a00120a08041206083212021a00120a08051206081412021a00",
                         Hex(GuildProtocol.BuildShop(accounts: 1)));
            Assert.Equal(20, GuildOracles.PriceFor(1, 1));
            Assert.Equal(200, GuildOracles.PriceFor(3, 1));
            Assert.Equal(50, GuildOracles.PriceFor(4, 1));
            Assert.Equal(800, GuildOracles.PriceFor(3, 4));
        }

        /// <summary>
        /// What is left to activate (jkv) and the purchase acknowledgement: the purchase of oracle 1 with its
        /// one-day deadline, byte for byte against the capture.
        /// </summary>
        [Fact]
        public void The_pending_oracle_is_the_capture()
        {
            Assert.Equal("0a24122208051a1e323032362d30392d30325432333a31333a30372e3437393237363538335a1001",
                         Hex(GuildProtocol.BuildPendingOracle(1, "2026-09-02T23:13:07.479276583Z")));
            Assert.Equal("0801", Hex(GuildProtocol.BuildShopBought(1)));
            Assert.Empty(GuildProtocol.BuildShopRefused());
            Assert.Equal("089204", Hex(GuildProtocol.BuildGuildKamas(530)));
        }

        /// <summary>
        /// The alteration the oracle sets on being activated (lzs): the capture's «Oráculo de saber»
        /// starts and ends where it says, with its two hours. The two detail blocks it
        /// carries there are not reproduced -only one of the five oracles is measured-, so
        /// the fields that do go are compared.
        /// </summary>
        [Fact]
        public void The_oracle_puts_its_alteration_for_two_hours()
        {
            Assert.Equal(859, GuildOracles.Of(1).Alteration);

            // The capture's lzs starts the same -«0a31 088d81fef9853410db06»- and ends with the
            // same «28e0dab4fd8534»: what goes in between is its two detail blocks, which are not
            // reproduced. What does go, goes where it goes.
            string lzs = Hex(GuildProtocol.BuildAlteration(859, 1788304392333, 1788311580000));
            Assert.Equal("0a13088d81fef9853410db06200228e0dab4fd8534", lzs);
            Assert.Contains("088d81fef9853410db06", lzs);        // from, and alteration 859
            Assert.Contains("28e0dab4fd8534", lzs);              // until, two hours later
            Assert.Equal(2, GuildOracles.HoursActive);
            Assert.Equal(24, GuildOracles.HoursToActivate);
        }

        /// <summary>The contribution (jle): ten thousand kamas and the three left, from the capture.</summary>
        [Fact]
        public void The_contribution_is_the_capture()
        {
            Assert.Equal("08904e1003", Hex(GuildProtocol.BuildContribution(10000, 3)));
            Assert.Equal(10000, GuildStore.ContributionKamas);
            Assert.Equal(10, GuildStore.ContributionGuildKamas);
        }

        /// <summary>
        /// Contributions are counted per week and the week starts on Tuesday, which is when
        /// the game resets the weekly things.
        /// </summary>
        [Fact]
        public void The_week_starts_on_tuesday()
        {
            Assert.Equal("2026-09-15", GuildStore.WeekOf(new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero)));
            Assert.Equal("2026-09-15", GuildStore.WeekOf(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)));
            Assert.Equal("2026-09-08", GuildStore.WeekOf(new DateTimeOffset(2026, 9, 14, 23, 0, 0, TimeSpan.Zero)));
        }

        /// <summary>
        /// The store, over a pass-through base: creating leaves the founder inside with rank 1, the
        /// guild is read by his character and leaving takes him out.
        /// </summary>
        [Fact]
        public void A_guild_survives_being_written_and_read_back()
        {
            string file = Path.Combine(Path.GetTempPath(), $"jondo-guild-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={file}";
            try
            {
                Assert.Null(GuildStore.GuildOf(7001));

                var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
                Assert.True(guild.Id > 0);
                Assert.Equal(1, guild.Level);

                var read = GuildStore.GuildOf(7001);
                Assert.NotNull(read);
                Assert.Equal("Jondo", read.Name);
                Assert.Equal(165, read.EmblemSymbol);
                Assert.Equal(9476018, read.EmblemSymbolRgb);
                Assert.Equal(1, GuildStore.RankOf(7001));

                var member = Assert.Single(GuildStore.Members(guild.Id));
                Assert.Equal(7001, member.CharacterId);
                Assert.Equal(1, member.Rank);

                Assert.Equal(guild.Id, GuildStore.Leave(7001).Id);
                Assert.Null(GuildStore.GuildOf(7001));
                Assert.Equal(0, GuildStore.RankOf(7001));
                Assert.Empty(GuildStore.Members(guild.Id));
            }
            finally
            {
                GuildStore.ConnectionStringOverride = null;
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { File.Delete(file); } catch (IOException) { }
            }
        }

        /// <summary>
        /// An accepted application puts the candidate in at rank 4 -the one carried in the capture by whoever
        /// joined that way- and takes the application away with it.
        /// </summary>
        [Fact]
        public void An_application_accepted_puts_the_newcomer_in_at_the_bottom_rank()
        {
            string file = Path.Combine(Path.GetTempPath(), $"jondo-guild-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={file}";
            try
            {
                var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);

                GuildStore.Apply(7002, guild.Id, "Hola dame gremio o te baneo");
                var application = Assert.Single(GuildStore.Applications(guild.Id));
                Assert.Equal(7002, application.CharacterId);
                Assert.Equal("Hola dame gremio o te baneo", application.Message);
                Assert.NotNull(GuildStore.ApplicationOf(guild.Id, 7002));

                GuildStore.Join(7002, guild.Id);
                Assert.Equal(GuildStore.RankNewcomer, GuildStore.RankOf(7002));
                Assert.Equal(4, GuildStore.RankNewcomer);
                Assert.Equal(2, GuildStore.Members(guild.Id).Count);
                Assert.Empty(GuildStore.Applications(guild.Id));
            }
            finally
            {
                GuildStore.ConnectionStringOverride = null;
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { File.Delete(file); } catch (IOException) { }
            }
        }

        /// <summary>
        /// Contributing raises the guild's kamas ten at a time and runs out at five per
        /// week; with them an oracle is bought, which deducts them and leaves a deadline to
        /// activate it.
        /// </summary>
        [Fact]
        public void Contributions_feed_the_guild_kamas_and_the_shop_spends_them()
        {
            string file = Path.Combine(Path.GetTempPath(), $"jondo-guild-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={file}";
            try
            {
                var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
                Assert.Equal(0, GuildStore.GuildOf(7001).GuildKamas);
                Assert.Equal(5, GuildStore.ContributionsLeft(7001));

                Assert.Equal(4, GuildStore.Contribute(7001, guild.Id));
                Assert.Equal(10, GuildStore.GuildOf(7001).GuildKamas);
                for (int i = 0; i < 4; i++) GuildStore.Contribute(7001, guild.Id);
                Assert.Equal(50, GuildStore.GuildOf(7001).GuildKamas);
                Assert.Equal(0, GuildStore.ContributionsLeft(7001));
                Assert.Equal(-1, GuildStore.Contribute(7001, guild.Id));   // the sixth does not go in
                Assert.Equal(50, GuildStore.GuildOf(7001).GuildKamas);

                // The divine one costs 200 per account: with 50 it does not reach and nothing is touched.
                Assert.False(GuildStore.SpendGuildKamas(guild.Id, GuildOracles.PriceFor(3, 1)));
                Assert.Equal(50, GuildStore.GuildOf(7001).GuildKamas);

                // The knowledge one does: 20, and 30 are left.
                Assert.True(GuildStore.SpendGuildKamas(guild.Id, GuildOracles.PriceFor(1, 1)));
                Assert.Equal(30, GuildStore.GuildOf(7001).GuildKamas);

                var deadline = DateTimeOffset.UtcNow.AddHours(GuildOracles.HoursToActivate);
                GuildStore.BuyOracle(guild.Id, 1, deadline);
                Assert.NotNull(GuildStore.OracleDeadline(guild.Id, 1));
                Assert.Null(GuildStore.OracleDeadline(guild.Id, 2));
            }
            finally
            {
                GuildStore.ConnectionStringOverride = null;
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }
}
