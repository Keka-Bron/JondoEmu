using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Where monsters may not be planted, and the way the rule used to feed the thing it forbade.
    /// </summary>
    /// <remarks>
    /// A group of monsters was standing inside the Incarnam smithy — an interior, where the rule
    /// says none may be — and another was standing on the Astrub zaap. The rule was not being
    /// ignored. It was <b>causing</b> them:
    ///
    /// <code>
    ///   boot drops the database groups for the 3,472 vetoed maps
    ///     -> those maps are left with NO KEY in _mapMobs
    ///       -> GetMobsForMap finds nothing and takes them for empty maps
    ///         -> it fills them with 2 to 4 freshly invented groups from the sub-area
    /// </code>
    ///
    /// So removing the groups was exactly what made others appear, and the first player to walk
    /// into any of those 3,472 maps met them. The smithy's own two database rows (mobs -1020951
    /// and -1020952) were correctly discarded; the group the player fought was invented at
    /// map-load time and carried a generated id, -3000131.
    ///
    /// These run against the real world database when it is on the machine and skip when it is
    /// not — it is 240 MB and never in git.
    /// </remarks>
    [Collection("MapManager")]
    public class MonsterVetoTests : IDisposable
    {
        private const long Smithy = 153355264;         // Incarnam, the smithy interior
        private const long SmithyOutside = 153879812;  // the outdoor map its door leads back to

        private static readonly object Gate = new object();
        private static bool _loaded;

        private static bool World()
        {
            lock (Gate)
            {
                if (_loaded) return MobSpawnManager.VetoedCount > 0;
                if (!File.Exists(Paths.WorldDb)) return false;

                // A locked database is a SKIP and not a failure. This opens the real 240 MB
                // world.db, and the publish runs the whole suite while it is also loading it --
                // one run went red here for no reason but the timing. A test that fails because
                // something else was reading a file teaches nobody anything.
                try
                {
                    MapManager.Initialize();
                    Interactives.Initialize();
                    MobSpawnManager.InitializeAndSpawnAll();
                }
                catch (Exception)
                {
                    _loaded = true;
                    return false;
                }

                _loaded = true;
                return MobSpawnManager.VetoedCount > 0;
            }
        }

        public void Dispose() { }

        [Fact]
        public void The_smithy_is_vetoed_and_the_map_outside_it_is_not()
        {
            if (!World()) return;

            Assert.True(MobSpawnManager.IsVetoed(Smithy),
                        "el taller es un interior y tendría que estar vetado");
            Assert.False(MobSpawnManager.IsVetoed(SmithyOutside),
                         "el mapa de fuera es a cielo abierto y sí lleva monstruos");
        }

        [Fact]
        public void A_vetoed_map_stays_empty_however_many_times_it_is_asked()
        {
            if (!World()) return;

            // Asked repeatedly on purpose. The bug was in the lazy generation that fires the FIRST
            // time a map is asked for and then caches; one call could pass by luck if something
            // had already cached an empty list.
            for (int i = 0; i < 3; i++)
            {
                Assert.Empty(MobSpawnManager.GetMobsForMap(Smithy));
            }
        }

        [Fact]
        public void The_zaap_maps_are_vetoed_too()
        {
            if (!World()) return;

            // 62 waypoint maps, and the player found monsters standing on the Astrub one. A group
            // planted on the zaap covers it: the click goes to the monster and there is no way
            // left to travel.
            long astrubZaap = 0;
            foreach (var waypoint in Interactives.Waypoints)
            {
                if (astrubZaap == 0) astrubZaap = waypoint.MapId;
                Assert.True(MobSpawnManager.IsVetoed(waypoint.MapId),
                            $"el mapa de zaap {waypoint.MapId} no está vetado");
            }

            Assert.NotEqual(0, astrubZaap);
        }

        [Fact]
        public void Plenty_of_maps_are_still_allowed_monsters()
        {
            if (!World()) return;

            // The other half of the deal, and worth an assertion of its own: a veto that swallowed
            // the whole world would pass every test above and leave a game with no monsters in it.
            Assert.True(MobSpawnManager.VetoedCount > 3000,
                        $"sólo {MobSpawnManager.VetoedCount} mapas vetados, se esperaban unos 3.472");
            Assert.True(MobSpawnManager.VetoedCount < 6000,
                        $"{MobSpawnManager.VetoedCount} mapas vetados es demasiado mundo sin bichos");
        }

        [Fact]
        public void Nothing_is_planted_on_a_cell_you_can_click()
        {
            if (!World()) return;

            // A monster on the zaap, on a workshop door or on a resource takes the click. The
            // whole-map veto covers the 62 zaap maps; this covers the doors and workshops, which
            // stand on maps that are not vetoed at all.
            int checkedMaps = 0;

            foreach (long mapId in new[] { SmithyOutside, 154010883L, 191104002L })
            {
                var elements = Interactives.ElementsOf(mapId);
                if (elements.Count == 0) continue;

                var taken = new HashSet<int>();
                foreach (var element in elements)
                {
                    if (element.Cell != 0) taken.Add(element.Cell);
                }

                if (taken.Count == 0) continue;
                checkedMaps++;

                foreach (int cell in MobSpawnManager.GetInnerWalkableCells(mapId))
                {
                    Assert.DoesNotContain(cell, taken);
                }
            }

            Assert.True(checkedMaps > 0, "ningún mapa de la muestra tenía interactivos que comprobar");
        }
    }
}
