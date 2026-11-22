using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Giant Kralamoure's lair on [-60,-8]: its gate (element 453562) was never declared, so
    /// never drawn, and there was no way in. Its plate in front of the lair (cell 376) opens it
    /// for forty minutes, and the gate itself is the passage into the first room while it stands open.
    /// </summary>
    [Collection("MapManager")]
    public class KralamoureGateTests
    {
        private const long Lair = 162056;
        private const int Gate = 453562, Plate = 376, FirstRoom = 26738688;

        [Fact]
        public void The_lair_has_a_plate_gate_that_announces_its_opening()
        {
            PlateGates.Forget();
            var gate = PlateGates.On(Lair);
            if (gate == null) return;   // no content

            Assert.Equal(new[] { Gate }, gate.Elements);
            Assert.Equal(new[] { Plate }, gate.Plates);
            Assert.Empty(gate.Obstacles);
            Assert.Equal(System.TimeSpan.FromMinutes(40), gate.OpenFor);
            Assert.Equal((0, 1479), gate.Announce);
            // Drawn with a state its element stopped being clickable: it keeps its map drawing.
            Assert.False(gate.DrawsState);
            Assert.Equal(0, PlateGates.StateOf(Lair));
        }

        [Fact]
        public void The_gate_lets_nobody_through_while_shut()
        {
            PlateGates.Forget();
            if (PlateGates.On(Lair) == null) return;   // no content

            Assert.True(PlateGates.IsGateElement(Lair, Gate));
            Assert.False(PlateGates.IsOpen(Lair));
            Assert.False(PlateGates.MayPass(Lair, Gate));
            Assert.True(PlateGates.MayPass(Lair, 518297));   // anything else on the map
        }

        [Fact]
        public void The_gate_is_the_passage_into_the_first_room()
        {
            TeleportManager.Initialize();
            if (!TeleportManager.TryGet(Lair, Gate, out var route)) return;   // no content

            Assert.Equal(FirstRoom, route.DestinationMapId);
        }

        [Fact]
        public async Task Staff_can_force_the_gate_open_without_its_plate()
        {
            PlateGates.Forget();
            if (PlateGates.On(Lair) == null) return;   // no content

            try
            {
                Assert.True(await PlateGates.ForceOpenAsync(Lair, "test"));
                Assert.True(PlateGates.IsOpen(Lair));
                Assert.True(PlateGates.MayPass(Lair, Gate));
                Assert.False(await PlateGates.ForceOpenAsync(Lair, "test"));
            }
            finally
            {
                PlateGates.Forget();
            }
        }

        [Fact]
        public void The_typed_map_keeps_the_gate_placement_even_when_it_also_offers_a_passage()
        {
            Interactives.Initialize();
            InteractiveRegistry.Initialize();
            var gate = InteractiveRegistry.OnMap(Lair).Single(element => element.Element.Id == Gate);
            Assert.Contains(gate.Actions, action => action.Kind == InteractiveActionKind.Teleport);
            var snapshot = MapInteractiveMessages.Snapshot(Lair, 0);
            Assert.Contains(snapshot.Elements, element => element.ElementId == Gate);
            Assert.Equal(PlateGates.StateOf(Lair), Assert.Single(snapshot.States, state => state.ElementId == Gate).State);
        }
    }
}
