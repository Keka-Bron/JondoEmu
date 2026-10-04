using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Lo que la ventana de gremio pide y recibe, byte a byte contra las capturas de «Jondo»:
    /// la entrada de un miembro con su clase, sus puntos, sus gremichas y su nota; los rangos
    /// después de editarlos; el diario; y la ficha del anuario.
    /// </summary>
    [Collection("guild raids")]
    public class GuildWindowTests : IDisposable
    {
        private readonly string _file;

        public GuildWindowTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-ventana-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
        }

        public void Dispose()
        {
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        private static readonly GuildStore.Guild Jondo = new()
        {
            Id = 42043, Name = "Jondo", Level = 1,
            EmblemSymbol = 165, EmblemSymbolColor = 8, EmblemBackground = 16744448, EmblemSymbolRgb = 9476018,
        };

        /// <summary>El fundador tal como sale en el jgu de la fundación: sacrógrito, 8.094 puntos, sin gremichas ni nota.</summary>
        private static GuildStore.Member SacriMaster(string note = "", long noteMs = 0) => new()
        {
            CharacterId = 302677754146, GuildId = 42043, Rank = 1, JoinedUtcMs = 1786567982202, Note = note, NoteMs = noteMs,
        };

        [Fact]
        public void The_founders_row_is_the_capture()
        {
            Assert.Equal("0a3d0a34120c53616372692d4d617374657218e2022a211001180120faf8ffbdff33300b3a09089e3f1200180342004202080150a2dab71f10a28280c8e708",
                         Hex(GuildProtocol.BuildMember(SacriMaster(), "Sacri-Master", 354, 65924386, breed: 11, achievementPoints: 8094)));
        }

        /// <summary>
        /// Y el mismo, puesto al día tras una contribución y la nota «hola»: el jgz de «muchas
        /// acciones», con las gremichas {10, 10} y la nota con su hora.
        /// </summary>
        [Fact]
        public void The_updated_row_carries_the_note_and_the_contributions()
        {
            Assert.Equal("124e0a45120c53616372692d4d617374657218e2022a321001180120faf8ffbdff33300b3a1a089e3f1204080a100a1803420d0a04686f6c6110808b88bfff334202080150a2dab71f10a28280c8e708",
                         Hex(GuildProtocol.BuildMemberUpdated(SacriMaster("hola", 1786570212736), "Sacri-Master", 354, 65924386,
                                                              breed: 11, achievementPoints: 8094, contributed: 10)));
        }

        /// <summary>
        /// Los rangos después de renombrar el 1, tocar los permisos del 2 y crear un quinto en el
        /// tercer puesto: el jco de 192 bytes de la captura, con el 4 corrido al cuarto.
        /// </summary>
        [Fact]
        public void The_edited_ranks_are_the_capture()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            var ranks = GuildStore.Ranks(guild.Id);
            Assert.Equal(4, ranks.Count);
            Assert.Equal(Hex(GuildProtocol.BuildDefaultRanks()), Hex(GuildProtocol.BuildRanks(ranks)));

            // jct: «Tesorero», con el f4 vacío, que deja el icono como estaba.
            var first = ranks.Single(r => r.Id == 1);
            first.Name = "Tesorero";
            GuildStore.SaveRank(first);

            // jct: «Test rango»; luego jck con la lista nueva del rango 2.
            var second = ranks.Single(r => r.Id == 2);
            second.Name = "Test rango";
            second.Rights = new byte[] { 0x01, 0x05, 0x06, 0x26, 0x07, 0x27, 0x08, 0x28, 0x29, 0x0d, 0x0e, 0x0f, 0x17, 0x18, 0x19 };
            GuildStore.SaveRank(second);

            // jcv: «Rango personalizado», icono 102, en el orden 3.
            var created = GuildStore.CreateRank(guild.Id, "Rango personalizado", 102, 3);
            Assert.Equal(5, created.Id);

            Assert.Equal("123012085465736f7265726f1a1e1a1c0102050607080d0e0f1718191a1d1e1f202122232425262728292a2b2202107428011229120a546573742072616e676f1a1308011a0f0105062607270828290d0e0f1718192204107318012802121f12116775696c642e72616e6b2e332e6e616d651a02080122041072180228031221121352616e676f20706572736f6e616c697a61646f1a0208012204106618032805121d12116775696c642e72616e6b2e342e6e616d651a002204107518042804",
                         Hex(GuildProtocol.BuildRanks(GuildStore.Ranks(guild.Id))));
        }

        /// <summary>El diario de «Jondo»: la fundación y las dos líneas de Hiierbita-Xx.</summary>
        [Fact]
        public void The_log_is_the_capture()
        {
            var entries = new List<GuildStore.LogEntry>
            {
                new() { GuildId = 42043, WhenMs = 1786567982200, Kind = GuildStore.LogFounded },
                new() { GuildId = 42043, WhenMs = 1786568036573, Kind = 2, CharacterId = 182801072418, Name = "Hiierbita-Xx" },
                new() { GuildId = 42043, WhenMs = 1786568106484, Kind = GuildStore.LogJoined, CharacterId = 182801072418, Name = "Hiierbita-Xx" },
            };
            Assert.Equal("0a0f58bbc8028a01009801f8f8ffbdff330a2658bbc8029801dda183beff33a2011710a282acfea8051a0c4869696572626974612d587820020a2458bbc8029801f4c387beff33a2011510a282acfea8051a0c4869696572626974612d5878",
                         Hex(GuildProtocol.BuildLog(entries)));

            // Y el almacén lo escribe solo: fundar es una línea, entrar es otra.
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            GuildStore.Join(7002, guild.Id);
            var log = GuildStore.LogOf(guild.Id);
            Assert.Equal(2, log.Count);
            Assert.Equal(GuildStore.LogFounded, log[0].Kind);
            Assert.Equal(GuildStore.LogJoined, log[1].Kind);
            Assert.Equal(7002, log[1].CharacterId);
        }

        /// <summary>La ficha del anuario, vacía y escrita, las dos de la captura de fundar «Jondo».</summary>
        [Fact]
        public void The_profile_is_the_capture()
        {
            Assert.Equal(Hex(GuildProtocol.BuildEmptyGuildMessage(42043)), Hex(GuildProtocol.BuildProfile(Jondo, null, "Sacri-Master")));
            Assert.Equal("120b1801280232010150bbc802", Hex(GuildProtocol.BuildProfile(Jondo, null, "")));

            var written = new GuildStore.Profile
            {
                GuildId = 42043, WhenMs = 1786568020893, Description = "Hola!", MinLevel = 20, MaxLevel = 100,
                Tags = new byte[] { 0x1d, 0x13, 0x08, 0x0e, 0x1a, 0x12, 0x0b, 0x09, 0x10 },
                F5 = 2, F6 = new byte[] { 0x03 }, Title = "Dragon Ball",
            };
            Assert.Equal("1241089da782beff331205486f6c6121181422091d13080e1a120b09102802320103420c53616372692d4d6173746572486450bbc8026a0b447261676f6e2042616c6c",
                         Hex(GuildProtocol.BuildProfile(Jondo, written, "Sacri-Master")));

            // Y el almacén la guarda entera.
            GuildStore.SaveProfile(written);
            var back = GuildStore.ProfileOf(42043);
            Assert.Equal("Dragon Ball", back.Title);
            Assert.Equal(written.Tags, back.Tags);
            Assert.Equal(20, back.MinLevel);
        }

        /// <summary>Las contribuciones que quedan y el jff de un gremio nuevo, como en la apertura.</summary>
        [Fact]
        public void The_window_opening_frames_are_the_capture()
        {
            Assert.Equal("0805", Hex(GuildProtocol.BuildContributionsLeft(5)));
            Assert.Equal("", Hex(GuildProtocol.BuildContributionsLeft(0)));
            Assert.Equal("1a00", Hex(GuildProtocol.BuildNoBenefits()));
        }

        /// <summary>
        /// Belonging, as world entry says it (jhe): the capture's frame of «Jondo» after two
        /// contributions ("entrar a combate-desconectarse-reconectar", frame 180), all but its
        /// unknown f4.
        /// </summary>
        [Fact]
        public void Belonging_is_the_world_entrys_jhe_and_not_the_jgw_of_joining()
        {
            const string captured = "0a200a111a0f08a5011008188080fe0728b2afc20410bbc8021a054a6f6e646f200110011814" + "20ff0d";
            Assert.Equal(captured[..^6], Hex(GuildProtocol.BuildMembership(Jondo, rank: 1, contribution: 20)));
        }

        /// <summary>The next weekly reset (jez), against the five captures that ask for it.</summary>
        [Theory]
        [InlineData("2026-08-09T16:56:00Z", "2026-08-11T05:00:00Z")]
        [InlineData("2026-08-12T21:50:00Z", "2026-08-18T05:00:00Z")]
        [InlineData("2026-08-15T18:24:00Z", "2026-08-18T05:00:00Z")]
        [InlineData("2026-08-29T21:03:00Z", "2026-09-01T05:00:00Z")]
        [InlineData("2026-09-01T23:12:00Z", "2026-09-08T05:00:00Z")]
        public void The_week_starts_again_on_tuesday_at_five(string asked, string reset)
        {
            var now = DateTime.Parse(asked, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            Assert.Equal("0a14" + Hex(System.Text.Encoding.ASCII.GetBytes(reset)), Hex(GuildProtocol.BuildWeeklyReset(now)));
        }

        /// <summary>The tabs of a guild with nothing in them, as the captures answer them.</summary>
        [Fact]
        public void An_empty_tab_is_the_captures()
        {
            Assert.Equal("0a00", Hex(GuildProtocol.BuildEmptyTab(1)));   // jfz, jgq, ice
            Assert.Equal("1a00", Hex(GuildProtocol.BuildEmptyTab(3)));   // jei
            Assert.Equal("", Hex(GuildProtocol.BuildEmptyTab(0)));       // jfs, jfr, hxm
        }

        /// <summary>
        /// Opening the window answers the chest's tabs and the header, and the members only when
        /// the jml asks for them -- never the jgw of joining, which printed "acabas de unirte al
        /// gremio" at every tab.
        /// </summary>
        [Fact]
        public async Task Opening_the_window_never_says_you_have_just_joined()
        {
            GuildStore.Create(7003, "Jondo", 165, 8, 16744448, 9476018);
            await using var wire = await global::Jondo.Unity.Tests.Combat.PortalTests.Wire.Open(7003);
            using (SessionContext.Push(wire.Session))
            {
                await GuildHandler.OpenWindowAsync(wire.Session.Stream!);
                await GuildHandler.MembersAsync(wire.Session.Stream!, ConnectionProtocol.Push(Op.Jml, Array.Empty<byte>()));
            }

            var ops = (await wire.Drain()).Select(f => f.Op).ToList();
            Assert.Equal(new[] { Op.Ivl, Op.Jhh }, ops);
        }

        /// <summary>Las gremichas salen de las contribuciones: diez por cada una, en total.</summary>
        [Fact]
        public void Contributions_become_gremichas()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            Assert.Equal(0, GuildStore.ContributedBy(7001));
            GuildStore.Contribute(7001, guild.Id);
            Assert.Equal(10, GuildStore.ContributedBy(7001));
            GuildStore.Contribute(7001, guild.Id);
            Assert.Equal(20, GuildStore.ContributedBy(7001));
        }
    }
}
