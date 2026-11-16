using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Founding a guild the way the game does it: the altar of the Guild Temple opens the
    /// editor, and the founder comes out of the jjg with the guild's name under his own.
    /// </summary>
    /// <remarks>
    /// Against the capture «comprar gremialogema-crear gremio-...»: iwo {3597, 480310} on map
    /// 106169344, iwn with skill 184, jjc, and after the jjg a jsn whose actor carries the guild
    /// as option f5 { f4 { block } }. In the same collection as the other guild ones because
    /// they share the pass-through store.
    /// </remarks>
    [Collection("guild raids")]
    public class GuildFoundingTests : IDisposable
    {
        private readonly string _file;

        public GuildFoundingTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-fundar-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
        }

        public void Dispose()
        {
            GuildStore.ConnectionStringOverride = null;
            TestDatabases.ReleasePool(_file);
            try { File.Delete(_file); } catch (IOException) { }
        }

        /// <summary>
        /// The altar is in the data of the map where the capture clicks it: 480310, on 326 of the
        /// Guild Temple. And the three founding opcodes have their name.
        /// </summary>
        [Fact]
        public void The_founding_altar_is_where_the_capture_pressed_it()
        {
            Assert.Equal("jjc", Op.Jjc);
            Assert.Equal("jhq", Op.Jhq);
            Assert.Equal("jjs", Op.Jjs);
            Assert.Equal(184, GuildHandler.FoundingSkill);

            if (!File.Exists(Paths.InteractiveElementsJson)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.InteractiveElementsJson));
            var elements = doc.RootElement.GetProperty(GuildHandler.FoundingMap.ToString());
            var altar = elements.EnumerateArray()
                .Single(e => e.GetProperty("e").GetInt32() == GuildHandler.FoundingAltar);
            Assert.Equal(326, altar.GetProperty("c").GetInt32());
        }

        /// <summary>
        /// The actor of whoever has a guild carries the guild block as its first option, and that of
        /// whoever does not, does not. It is what draws the guild's name under the character's on the
        /// map, for him and for the others.
        /// </summary>
        [Fact]
        public void A_guilded_actor_carries_its_guild_block()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);

            var founder = new DbCharacter { Id = 7001, Name = "Hero-Example", Level = 200, Breed = 11 };
            var loner = new DbCharacter { Id = 7002, Name = "Solo", Level = 200, Breed = 11 };

            var withGuild = GuildOption(MapActorMessages.Player(founder, 341, 5, 60000001));
            Assert.NotNull(withGuild);
            Assert.Equal(guild.Id, withGuild!.Id);
            Assert.Equal("Jondo", withGuild.Name);
            Assert.Equal(1, withGuild.Level);

            Assert.Null(GuildOption(MapActorMessages.Player(loner, 341, 5, 60000001)));
        }

        private static GuildInformation? GuildOption(ActorPositionInformation actor)
            => MapActorAssertions.Human(actor).Options.Select(option => option.OptionCase)
                .OfType<HumanOptionCaseGuildInformation>().SingleOrDefault()?.Value;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async System.Threading.Tasks.Task Founding_spends_the_item_then_sends_typed_empty_houses_before_the_remaining_burst(bool fail)
        {
            const long founder = 9_969_000_001;
            var item = DatabaseManager.AddPieceToInventory(founder, GuildHandler.GuildalogemTemplate, "[]");
            Assert.NotNull(item);
            var codec = new FoundingCodec { Fail = fail };
            await using var pipe = await Economy.ClientPipe.OpenAsync(8_969_000_001, founder, 91_969_001, codec: codec);
            try
            {
                using var context = SessionContext.Push(pipe.Session);
                using var turn = await pipe.Session.EnterTurnAsync();
                Equipment.Add(item.Uid, GuildHandler.GuildalogemTemplate, 1, Equipment.Bag, null);
                var founding = GuildHandler.CreateFromCommandAsync(pipe.ToClient, founder, "TypedHouseGuild");
                if (fail) await Assert.ThrowsAsync<IOException>(() => founding);
                else Assert.Null(await founding.WaitAsync(TimeSpan.FromSeconds(5)));
                await pipe.Next(Op.Ium);
                Assert.Null(Equipment.ByUid(item.Uid));
                Assert.NotNull(GuildStore.GuildOf(founder));
                Assert.Empty(Assert.IsType<GuildHousesEvent>(codec.Frames[0].Content).Houses);
                Assert.Null(codec.Frames[0].Uid);
                if (!fail)
                {
                    Assert.Equal(new byte[] { 0x73, 1 }, await Packet(pipe));
                    await pipe.Next(Op.Jhq);
                    Assert.Equal(new byte[] { 0x73, 2 }, await Packet(pipe));
                    await pipe.Next(Op.Jgw);
                    await pipe.Next(Op.Khi);
                    await pipe.Next(Op.Jgu);
                    Assert.Equal(new byte[] { 0x73, 3 }, await Packet(pipe));
                }
                else Assert.Single(codec.Frames);
                Assert.Equal(0, pipe.Session.UnoCadaVez.CurrentCount);
                Assert.False(pipe.FromServer.DataAvailable);
            }
            finally
            {
                using var db = new SqliteConnection(DatabaseManager.WorldConnectionString);
                db.Open();
                using var clean = db.CreateCommand();
                clean.CommandText = "DELETE FROM CharacterItems WHERE CharacterId = $founder;";
                clean.Parameters.AddWithValue("$founder", founder);
                clean.ExecuteNonQuery();
            }
        }

        private static async System.Threading.Tasks.Task<byte[]> Packet(Economy.ClientPipe pipe)
            => (await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5)))!;

        private sealed class FoundingCodec : IGameCodec
        {
            internal readonly System.Collections.Generic.List<GameFrame> Frames = [];
            internal bool Fail;
            public byte[] Encode(GameFrame frame)
            {
                Frames.Add(frame);
                if (Fail && frame.Content is GuildHousesEvent) throw new IOException("house reset encoding failed");
                return [0x73, checked((byte)Frames.Count)];
            }
            public GameFrame Decode(ReadOnlySpan<byte> packet) => throw new InvalidOperationException("Founding must not reparse packets.");
        }
    }
}
