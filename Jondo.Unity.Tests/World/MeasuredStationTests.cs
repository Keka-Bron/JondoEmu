using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Workshop stations and bins the official server declares in its map contents (packet
    /// captures, 2026-11-20/21) that Jondo declared with nothing to do: a potion table in Astrub, the
    /// smelter and stone polisher of 99353612, the Prismaradite maker, the peeling stations of Bonta and
    /// Brâkmar, and Amakna's bins (drawing 29506, «Vasculhar» 153).
    /// </summary>
    [Collection("MapManager")]
    public sealed class MeasuredStationTests
    {
        [Theory]
        [InlineData(192937988, 515516, 90, 23)]     // «Preparar uma poção»
        [InlineData(99353612, 474697, 27, 32)]      // «Fundir»
        [InlineData(99353612, 474696, 41, 48)]      // «Polir uma pedra»
        [InlineData(192940040, 518018, 12, 380)]    // «Criar um Prismaradita»
        [InlineData(217320454, 521662, -1, 22)]     // «Descascar», Bonta
        [InlineData(216663042, 522413, 49, 22)]     // «Descascar», Brâkmar
        public void The_element_is_declared_as_the_official_server_declares_it(long map, int element, int type, int skill)
        {
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            Assert.NotNull(MapManager.GetMapInfo(map));
            Interactives.Initialize();
            TeleportManager.Initialize();
            Resources.Initialize();
            Bins.Initialize();
            Workshops.Initialize();
            InteractiveRegistry.Initialize();

            var declared = MapInteractiveMessages.Snapshot(map, 0).Elements.Single(e => e.ElementId == element);
            Assert.Equal(type, declared.ElementTypeId);
            Assert.Contains(skill, declared.EnabledSkills.Select(s => s.SkillId));
        }

        /// <summary>Amakna's bins: «Vasculhar» on drawing 29506, as on the other cities' bins (their
        /// type is the bins' own, whatever the city).</summary>
        [Theory]
        [InlineData(88080663, 473902)]
        [InlineData(88212247, 473902)]
        [InlineData(88081177, 473904)]
        public void Amakna_s_bins_can_be_rummaged(long map, int element)
        {
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            Assert.NotNull(MapManager.GetMapInfo(map));
            Interactives.Initialize();
            TeleportManager.Initialize();
            Resources.Initialize();
            Bins.Initialize();
            Workshops.Initialize();
            InteractiveRegistry.Initialize();

            var declared = MapInteractiveMessages.Snapshot(map, 0).Elements.Single(e => e.ElementId == element);
            Assert.Contains(153, declared.EnabledSkills.Select(s => s.SkillId));
        }
    }
}
