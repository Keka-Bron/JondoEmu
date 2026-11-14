using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The borders the world graph sends somewhere other than the map next door
    /// (datos/border_passages_worldgraph_2.73.json).
    /// </summary>
    public class BorderPassageTests
    {
        private const long BridgeStart = 189399809;   // [16,-24], off the Astrub quarry's right edge
        private const long BridgeEnd = 189268737;     // [16,-26]
        private const long PandalaWest = 206307331;   // [19,-28]

        [Fact]
        public void The_bridges_first_half_leads_to_its_second()
        {
            Assert.True(BorderPassages.TryFind(BridgeStart, "right", 55, _ => true, out long map, out int cell));
            Assert.Equal(BridgeEnd, map);
            Assert.Equal(350, cell);
        }

        [Fact]
        public void The_bridges_second_half_leads_into_Pandala_and_back()
        {
            Assert.True(BorderPassages.TryFind(BridgeEnd, "right", 41, _ => true, out long map, out int cell));
            Assert.Equal(PandalaWest, map);
            Assert.Equal(462, cell);

            Assert.True(BorderPassages.TryFind(PandalaWest, "left", 462, _ => true, out map, out cell));
            Assert.Equal(BridgeEnd, map);
            Assert.Equal(41, cell);

            Assert.True(BorderPassages.TryFind(BridgeEnd, "left", 350, _ => true, out map, out _));
            Assert.Equal(BridgeStart, map);
        }

        [Fact]
        public void A_passage_whose_criterion_fails_is_not_taken()
        {
            // «PO<23515,1»: whoever holds quest 2157's marker 23515 does not cross into Pandala.
            Assert.False(BorderPassages.TryFind(BridgeEnd, "right", 41, _ => false, out _, out _));
        }

        [Fact]
        public void A_border_split_by_cells_leads_by_the_cell_it_is_crossed_from()
        {
            Assert.True(BorderPassages.TryFind(108793115, "right", 111, _ => true, out long upper, out _));
            Assert.Equal(108793118, upper);
            Assert.True(BorderPassages.TryFind(108793115, "right", 475, _ => true, out long lower, out _));
            Assert.Equal(108794651, lower);
            Assert.False(BorderPassages.TryFind(108793115, "right", 300, _ => true, out _, out _));
        }

        [Fact]
        public void The_passage_with_a_criterion_goes_before_the_plain_one()
        {
            Assert.True(BorderPassages.TryFind(195302408, "left", 447, _ => true, out long map, out _));
            Assert.Equal(195301384, map);
            Assert.True(BorderPassages.TryFind(195302408, "left", 447, _ => false, out map, out _));
            Assert.Equal(195302410, map);
        }

        [Fact]
        public void The_bridge_stays_shut_until_the_serum_is_in()
        {
            const long Quarry = 193201153;   // [12,-21], where the bridge starts
            string asked = "";
            Assert.True(ClosedBorders.Shut(Quarry, "right", c => { asked = c; return false; }));
            Assert.Contains("Qf=2149", asked);
            Assert.Contains("Qo>16321", asked);
            Assert.False(ClosedBorders.Shut(Quarry, "right", _ => true));
            // Its other borders, and the way back from the bridge, are open to everybody.
            Assert.False(ClosedBorders.Shut(Quarry, "left", _ => false));
            Assert.False(ClosedBorders.Shut(BridgeStart, "left", _ => false));
        }

        [Fact]
        public void A_map_with_no_such_border_has_none()
        {
            Assert.False(BorderPassages.TryFind(154010883, "right", 27, _ => true, out _, out _));
            Assert.Equal(0, BorderPassages.AnyPast(154010883, "right", 27));
        }
    }
}
