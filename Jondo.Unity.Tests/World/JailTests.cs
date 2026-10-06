using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The jail: the GM prison's floor read from its walkable cells, where a prisoner and his
    /// warden stand, what a prisoner cannot do, and the way in and out.
    /// </summary>
    [Collection("MapManager")]
    public class JailTests
    {
        private static int[] PrisonFloor()
        {
            using var cells = JsonDocument.Parse(File.ReadAllText(Paths.WalkableCellsJson));
            return cells.RootElement.GetProperty(Jail.MapId.ToString()).EnumerateArray().Select(c => c.GetInt32()).ToArray();
        }

        /// <summary>
        /// The GM prison is a corridor and four cells in its corners, none of them reachable from
        /// it on foot: what makes it a jail without a single cell written down.
        /// </summary>
        [Fact]
        public void The_prison_is_a_corridor_and_four_cells_walled_off_from_it()
        {
            var layout = Jail.LayoutOf(PrisonFloor())!;
            Assert.Equal(75, layout.Corridor.Count);
            Assert.Equal(new[] { 13, 14, 17, 17 }, layout.Cells.Select(c => c.Count).OrderBy(n => n).ToArray());
            // No square of a cell touches the corridor, diagonals counted.
            foreach (var cell in layout.Cells)
                foreach (int square in cell)
                    Assert.All(layout.Corridor, c => Assert.True(Math.Abs(MapGeometry.CellToPoint(c).X - MapGeometry.CellToPoint(square).X) > 1
                                                                 || Math.Abs(MapGeometry.CellToPoint(c).Y - MapGeometry.CellToPoint(square).Y) > 1));
        }

        /// <summary>
        /// The prisoner goes in a cell and his warden to the corridor beside its bars; a second
        /// prisoner gets a cell of his own.
        /// </summary>
        [Fact]
        public void A_prisoner_goes_in_a_cell_and_his_warden_beside_it_in_the_corridor()
        {
            var layout = Jail.LayoutOf(PrisonFloor())!;
            var (inside, outside) = Jail.PlacesFor(layout, Array.Empty<int>());
            var cell = layout.Cells.Single(c => c.Contains(inside));
            Assert.Contains(outside, layout.Corridor);
            Assert.True(cell.Min(c => MapGeometry.Distance(c, outside)) <= 2, $"the warden stands {cell.Min(c => MapGeometry.Distance(c, outside))} away");

            var (second, _) = Jail.PlacesFor(layout, new[] { inside, outside });
            Assert.DoesNotContain(second, cell);
        }

        /// <summary>A prisoner speaks on the general channel only; anybody else, on any.</summary>
        [Fact]
        public void A_prisoner_speaks_on_the_general_channel_only()
        {
            const long prisoner = 990_000_101;
            Jail.PutForTests(new Jail.Prisoner(prisoner, "Preso", DateTime.UtcNow.AddMinutes(5), 1, 1, 0));
            try
            {
                Assert.True(Jail.MaySpeakOn(prisoner, Jail.GeneralChannel));
                Assert.False(Jail.MaySpeakOn(prisoner, 2));
                Assert.True(Jail.MaySpeakOn(990_000_102, 2));
            }
            finally { Jail.ForgetForTests(prisoner); }
        }

        /// <summary>A sentence served is no sentence, even before the clock has let him out.</summary>
        [Fact]
        public void A_sentence_past_its_time_holds_nobody()
        {
            const long prisoner = 990_000_103;
            Jail.PutForTests(new Jail.Prisoner(prisoner, "Preso", DateTime.UtcNow.AddSeconds(-1), 1, 1, 0));
            try { Assert.False(Jail.IsJailed(prisoner)); }
            finally { Jail.ForgetForTests(prisoner); }
        }

        /// <summary>No command in jail, an administrator's neither: swallowed, and said why.</summary>
        [Fact]
        public async Task A_prisoner_cannot_use_commands()
        {
            const long prisoner = 990_000_104;
            await using var pipe = await ClientPipe.OpenAsync(990_000_104, prisoner, 1, "Preso");
            Jail.PutForTests(new Jail.Prisoner(prisoner, "Preso", DateTime.UtcNow.AddMinutes(5), 1, 1, 0));
            try
            {
                using (SessionContext.Push(pipe.Session))
                {
                    Assert.True(await CommandHandler.TryHandleAsync(pipe.ToClient, ".kamas 1000", 0, 990_000_104));
                    Assert.Equal(0, pipe.Session.State.Kamas);
                    await pipe.Next(Op.Lqn);
                    // A line that is no command still goes through as text.
                    Assert.False(await CommandHandler.TryHandleAsync(pipe.ToClient, "...vale", 0, 990_000_104));
                }
            }
            finally { Jail.ForgetForTests(prisoner); }
        }

        /// <summary>
        /// The whole way: the prisoner to a cell of the prison and his warden to the corridor; no
        /// teleport for the prisoner while inside; and when he is let out, back where he was.
        /// </summary>
        [Fact]
        public async Task In_and_out_of_jail()
        {
            if (!MapManager.WalkableCells.ContainsKey(Jail.MapId)) MapManager.Initialize();
            if (MapManager.GetMapInfo(Jail.MapId) == null) return;   // no world data

            Jail.Initialize();
            const long prisonerId = 990_000_105, wardenId = 990_000_106;
            const long astrub = 191105026;
            await using var prisoner = await ClientPipe.OpenAsync(990_000_105, prisonerId, astrub, "Preso");
            await using var warden = await ClientPipe.OpenAsync(990_000_106, wardenId, astrub, "Guardia");
            prisoner.Session.State.CellId = 300;
            try
            {
                var (refusal, sentence) = await Jail.LockUpAsync(prisoner.Session, warden.Session);
                Assert.Equal(Jail.Refusal.None, refusal);
                Assert.True(Jail.IsJailed(prisonerId));
                Assert.Equal((astrub, 300), (sentence!.ReturnMapId, sentence.ReturnCellId));

                var layout = Jail.LayoutOf(MapManager.WalkableCells[Jail.MapId])!;
                Assert.Equal(Jail.MapId, prisoner.Session.State.MapId);
                Assert.Contains(layout.Cells, c => c.Contains(prisoner.Session.State.CellId));
                Assert.Equal(Jail.MapId, warden.Session.State.MapId);
                Assert.Contains(warden.Session.State.CellId, layout.Corridor);
                Assert.False(Jail.IsJailed(wardenId));

                // Inside, every road out is shut.
                using (SessionContext.Push(prisoner.Session))
                    Assert.Equal(-1, await TeleportHandler.ToMapAsync(prisoner.ToClient, astrub, 300));
                Assert.Equal(Jail.MapId, prisoner.Session.State.MapId);

                // Twice is refused.
                Assert.Equal(Jail.Refusal.AlreadyIn, (await Jail.LockUpAsync(prisoner.Session, warden.Session)).Refusal);

                Assert.Equal(Jail.Refusal.None, await Jail.ReleaseAsync(prisonerId, "prueba"));
                Assert.False(Jail.IsJailed(prisonerId));
                Assert.Equal(astrub, prisoner.Session.State.MapId);
            }
            finally
            {
                if (Jail.IsJailed(prisonerId)) await Jail.ReleaseAsync(prisonerId, "prueba");
            }
        }

        /// <summary>
        /// The administrator's "Ir a la cárcel": to the corridor, as when he takes somebody in,
        /// but nobody is locked up -- and from there he leaves as from any map.
        /// </summary>
        [Fact]
        public async Task A_visitor_goes_to_the_corridor_and_is_free_to_leave()
        {
            if (!MapManager.WalkableCells.ContainsKey(Jail.MapId)) MapManager.Initialize();
            if (MapManager.GetMapInfo(Jail.MapId) == null) return;   // no world data

            Jail.Initialize();
            const long visitorId = 990_000_107;
            const long astrub = 191105026;
            await using var visitor = await ClientPipe.OpenAsync(990_000_107, visitorId, astrub, "Visita");

            Assert.Equal(Jail.Refusal.None, await Jail.VisitAsync(visitor.Session));
            var layout = Jail.LayoutOf(MapManager.WalkableCells[Jail.MapId])!;
            Assert.Equal(Jail.MapId, visitor.Session.State.MapId);
            Assert.Contains(visitor.Session.State.CellId, layout.Corridor);
            Assert.False(Jail.IsJailed(visitorId));

            using (SessionContext.Push(visitor.Session))
                Assert.NotEqual(-1, await TeleportHandler.ToMapAsync(visitor.ToClient, astrub, 300));
            Assert.Equal(astrub, visitor.Session.State.MapId);
        }
    }
}
