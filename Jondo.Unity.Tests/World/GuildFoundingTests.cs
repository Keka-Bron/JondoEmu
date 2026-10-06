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
            SqliteConnection.ClearAllPools();
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

            var founder = new DatabaseManager.DbCharacter { Id = 7001, Name = "Sacri-Master", Level = 200, Breed = 11 };
            var loner = new DatabaseManager.DbCharacter { Id = 7002, Name = "Solo", Level = 200, Breed = 11 };

            var withGuild = GuildOption(ConnectionProtocol.BuildPlayerActorBlock(founder, 341, 5, 65924386));
            Assert.NotNull(withGuild);
            Assert.Equal(guild.Id, Field(withGuild!, 2));
            Assert.Equal("Jondo", System.Text.Encoding.UTF8.GetString(
                withGuild!.Fields.Single(f => f.FieldNumber == 3 && f.WireType == 2).BytesValue));
            Assert.Equal(1, Field(withGuild!, 4));

            Assert.Null(GuildOption(ConnectionProtocol.BuildPlayerActorBlock(loner, 341, 5, 65924386)));
        }

        /// <summary>The f5 { f4 { ... } } of the actor's humanoid body, or null if it does not carry it.</summary>
        private static ProtoMessage? GuildOption(byte[] actor)
        {
            var root = ProtoMessage.Parse(actor);
            var details = ProtoMessage.Parse(root.Fields.Single(f => f.FieldNumber == 2).BytesValue);
            var kind = ProtoMessage.Parse(details.Fields.Single(f => f.FieldNumber == 1).BytesValue);
            var humanoid = ProtoMessage.Parse(kind.Fields.Single(f => f.FieldNumber == 5).BytesValue);
            var body = ProtoMessage.Parse(humanoid.Fields.Single(f => f.FieldNumber == 3).BytesValue);

            foreach (var option in body.Fields.Where(f => f.FieldNumber == 5 && f.WireType == 2))
            {
                var inner = ProtoMessage.Parse(option.BytesValue);
                var guild = inner.Fields.FirstOrDefault(f => f.FieldNumber == 4 && f.WireType == 2);
                if (guild != null) return ProtoMessage.Parse(guild.BytesValue);
            }

            return null;
        }

        private static long Field(ProtoMessage message, int number)
            => message.Fields.Single(f => f.FieldNumber == number && f.WireType == 0).VarIntValue;
    }
}
