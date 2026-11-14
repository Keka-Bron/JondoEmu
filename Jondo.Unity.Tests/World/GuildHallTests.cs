using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The guild halls: the client's seven and their looks, a guild's choice, what a room tells its
    /// visitor, and the frames against «en boton de gestion-elegir este recibidor para mi gremio» and
    /// «entrar en recibidor de gremio en amakna».
    /// </summary>
    [Collection("guild raids")]
    public class GuildHallTests : IDisposable
    {
        private readonly string _file;

        public GuildHallTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-recibidor-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
        }

        public void Dispose()
        {
            GuildStore.ConnectionStringOverride = null;
            TestDatabases.ReleasePool(_file);
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        [Fact]
        public void The_halls_and_their_looks_are_the_client_s()
        {
            Assert.Equal(7, GuildHalls.All.Count);
            var amakna = GuildHalls.ById(8)!;
            Assert.Equal(88212760, amakna.StreetMapId);
            Assert.Equal(99093249, amakna.FunctionRoom);
            Assert.Equal((99093249L, 410), (amakna.EntranceMapId, amakna.EntranceCellId));   // the capture's arrival
            Assert.Same(amakna, GuildHalls.OfRoom(99092225));
            Assert.Null(GuildHalls.OfRoom(88212760));

            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, GuildHalls.Themes.Select(t => t.Id));
            Assert.Equal(new[] { 1 }, GuildHalls.ThemesAtLevel(1));
            Assert.Equal(new[] { 1, 2 }, GuildHalls.ThemesAtLevel(6));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, GuildHalls.ThemesAtLevel(20));
        }

        /// <summary>Amakna's doors lead where the capture walked, onto the cells it stood on.</summary>
        [Fact]
        public void Amakna_s_doors_are_the_capture_s_walk()
        {
            var doors = GuildHalls.Doors().ToDictionary(d => (d.SourceMapId, d.ElementId));
            Assert.Equal((99092225L, 453), (doors[(99093249, 540260)].DestinationMapId, doors[(99093249, 540260)].DestinationCellId));
            Assert.Equal((99094273L, 274), (doors[(99093249, 540261)].DestinationMapId, doors[(99093249, 540261)].DestinationCellId));
            Assert.Equal((99091201L, 344), (doors[(99093249, 540555)].DestinationMapId, doors[(99093249, 540555)].DestinationCellId));
            Assert.Equal((99093249L, 445), (doors[(99092225, 540259)].DestinationMapId, doors[(99092225, 540259)].DestinationCellId));
            Assert.Equal((99093249L, 281), (doors[(99094273, 540376)].DestinationMapId, doors[(99094273, 540376)].DestinationCellId));
            Assert.Equal((99093249L, 201), (doors[(99091201, 540556)].DestinationMapId, doors[(99091201, 540556)].DestinationCellId));
            Assert.Equal(88212760, doors[(99093249, 540377)].DestinationMapId);
            Assert.All(doors.Values, d => Assert.Equal(184, d.SkillId));

            // One passage a cell, as the start's regression guard wants: Pandala's street has its
            // door twice on cell 283, and only one of the two is a passage.
            Assert.Equal(doors.Count, doors.Values.Select(d => (d.SourceMapId, d.SourceCellId)).Distinct().Count());
        }

        /// <summary>
        /// The standalone hall-change and staging frames. Typed map hall branches
        /// have independent fixtures in the Game protocol suite's GuildHallMappingTests.
        /// </summary>
        [Fact]
        public void The_hall_frames_are_the_captures()
        {
            Assert.Equal("1008", Hex(GuildProtocol.BuildHallChanged(8)));
            Assert.Equal("08641064180a", Hex(GuildRaidStagings.BuildPlay(GuildHalls.NeutralStaging, GuildRaidStagings.AtEnd)));
        }

        /// <summary>
        /// A guild's choice: its hall becomes its own to its members -- the look it had, the looks its
        /// level opens -- and stays neutral to anybody else; changing hall keeps the look.
        /// </summary>
        [Fact]
        public void A_chosen_hall_is_the_guild_s_own()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            Assert.False(GuildHalls.Visit(99093249, 7001)!.Value.Own);
            Assert.True(GuildHalls.Visit(99093249, 7001)!.Value.MayChoose);   // the leader
            Assert.Null(GuildHalls.Visit(88212760, 7001));                     // the street is no room

            GuildHalls.Choose(guild.Id, 8, DateTimeOffset.UtcNow);
            var visit = GuildHalls.Visit(99094273, 7001)!.Value;
            Assert.True(visit.Own);
            Assert.Equal(GuildHalls.DefaultTheme, visit.Theme);
            Assert.Equal(new[] { 1 }, visit.Themes);
            Assert.False(GuildHalls.Visit(99094273, 7999)!.Value.Own);         // no guild

            GuildHalls.SetTheme(guild.Id, 2);
            GuildHalls.Choose(guild.Id, 4, DateTimeOffset.UtcNow);
            Assert.Equal(2, GuildHalls.ChoiceOf(guild.Id)!.Theme);
            Assert.False(GuildHalls.Visit(99093249, 7001)!.Value.Own);
        }
    }
}
