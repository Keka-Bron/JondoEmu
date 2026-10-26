using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Gigalodón's floors as the client's maps have them: its lifts and passages, and the
    /// Luminarium's board. In the MapManager collection, as every test that loads the interactives.
    /// </summary>
    [Collection("MapManager")]
    public class GuildRaidMapTests
    {
        public GuildRaidMapTests() => Interactives.Initialize();

        private static bool HasWorld => DatabaseManager.MapsOfSubArea(Raids.Of(Raids.Gigalodon).Floors[0]).Count > 0
                                        && Interactives.ElementsOf(239081984).Count > 0;

        /// <summary>
        /// The lifts go to the nearest lift of the next floor: -1 [4,3] down to -2 [4,5], -2 [2,7] down
        /// to -3 [2,9], and the diving cage -5 [10,14] down to -6 [10,16]; the passages join -3 [4,12]
        /// to -4 [5,11] and -4 [9,12] to -5 [10,13].
        /// </summary>
        [Fact]
        public void The_floors_are_joined_by_their_lifts_and_passages()
        {
            if (!HasWorld) return;
            var routes = GuildRaidPassages.Derive();
            InteractiveTeleport From(long map, int gfx) => routes.Single(r => r.SourceMapId == map && r.GfxId == gfx);

            var down1 = From(239081229, GuildRaidPassages.LiftGfx);
            Assert.Equal(239081225, down1.DestinationMapId);
            Assert.Equal((GuildRaidPassages.LiftType, GuildRaidPassages.DownSkill), (down1.InteractiveType, down1.SkillId));
            Assert.Equal(239081229, From(239081225, GuildRaidPassages.LiftGfx).DestinationMapId);       // and back up
            Assert.Equal(239080707, From(239080199, GuildRaidPassages.LiftGfx).DestinationMapId);

            var cage = From(239079947, GuildRaidPassages.LiftGfx);
            Assert.Equal(239079951, cage.DestinationMapId);
            Assert.Equal(GuildRaidPassages.CageType, cage.InteractiveType);

            Assert.Equal(239079430, From(239081984, GuildRaidPassages.PassageGfx).DestinationMapId);
            Assert.Equal(539864, From(239081984, GuildRaidPassages.PassageGfx).ElementId);
            Assert.Equal(239079946, From(239083016, GuildRaidPassages.PassageGfx).DestinationMapId);
        }

        /// <summary>
        /// The Abyss starts in its outpost, [3,1], where the hatch from the surface comes out -- not on
        /// floor -1's lowest map by number, an arena with no position and no way out. Its ladder goes
        /// down under the building, to [3,2], and the ladder there, seen from [3,2] and [2,2], comes
        /// back up.
        /// </summary>
        [Fact]
        public void The_abyss_starts_in_its_outpost_and_leaves_by_its_ladder()
        {
            if (!HasWorld) return;
            var abyss = Raids.Of(Raids.Gigalodon);
            Assert.Equal(239080724L, GuildRaidManager.EntryMapOf(abyss));

            var routes = GuildRaidPassages.Derive();
            var down = routes.Single(r => r.SourceMapId == 239080724 && r.GfxId == GuildRaidPassages.LadderGfx);
            Assert.Equal(239080718L, down.DestinationMapId);
            Assert.Equal((GuildRaidPassages.LadderType, GuildRaidPassages.DownSkill), (down.InteractiveType, down.SkillId));
            foreach (long under in new[] { 239080718L, 239080206L })
            {
                var up = routes.Single(r => r.SourceMapId == under && r.GfxId == GuildRaidPassages.LadderGfx);
                Assert.Equal(239080724L, up.DestinationMapId);
                Assert.Equal(GuildRaidPassages.UpSkill, up.SkillId);
            }

            // And its hatch, up to the surface platform [3,0] and back down.
            var hatchUp = routes.Single(r => r.SourceMapId == 239080724 && r.GfxId == GuildRaidPassages.EntryGfx);
            Assert.Equal((239080726L, GuildRaidPassages.UpSkill), (hatchUp.DestinationMapId, hatchUp.SkillId));
            var hatchDown = routes.Single(r => r.SourceMapId == 239080726 && r.GfxId == GuildRaidPassages.HatchTopGfx);
            Assert.Equal((239080724L, GuildRaidPassages.DownSkill), (hatchDown.DestinationMapId, hatchDown.SkillId));
        }

        /// <summary>
        /// A floor's map whose written groups were all beaten stays empty -- it was refilled with the
        /// subarea's monsters on the next look, and floor -1 could never be cleared -- and a floor's
        /// groups are counted on its world maps only, not on its arenas, which the base fills and the spawner vetoes.
        /// </summary>
        [Fact]
        public void A_cleared_raid_map_stays_cleared_and_arenas_do_not_count()
        {
            if (!HasWorld) return;
            if (MapManager.GetMapInfo(239080724) == null) MapManager.Initialize();
            if (MobSpawnManager.LoadedGroups(239081229) == 0) MobSpawnManager.InitializeAndSpawnAll();
            if (MobSpawnManager.LoadedGroups(239081229) == 0) return;
            try
            {
                foreach (var group in MobSpawnManager.GetMobsForMap(239081229))
                    MobSpawnManager.RemoveMobGroup(239081229, group.MobId);
                Assert.Empty(MobSpawnManager.GetMobsForMap(239081229));

                int floor = Raids.Of(Raids.Gigalodon).Floors[0];
                Assert.True(GuildRaidManager.IsArena(239089933));
                Assert.False(GuildRaidManager.IsArena(239080724));
                var maps = DatabaseManager.MapsOfSubArea(floor);
                var (loaded, _) = GuildRaidManager.GroupsOn(floor);
                Assert.Equal(maps.Where(m => !GuildRaidManager.IsArena(m)).Sum(MobSpawnManager.LoadedGroups), loaded);
                // And the arenas hold none: the base fills them, the spawner vetoes them.
                Assert.Equal(0, maps.Where(GuildRaidManager.IsArena).Sum(MobSpawnManager.LoadedGroups));
            }
            finally
            {
                MobSpawnManager.RestoreMap(239081229);
            }
        }

        /// <summary>
        /// A fight on an Abyss floor goes to one of the floor's own arenas, the maps without position
        /// drawn for fighting; never to its bare test grid. An arena is fought on as it is, and the
        /// Gigalodón's is the outpost's twin.
        /// </summary>
        [Fact]
        public void The_abyss_fights_on_its_floors_arenas()
        {
            if (!HasWorld) return;
            if (MapManager.GetMapInfo(239080724) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(239080724) == null) return;

            long arena = MapManager.ResolveArenaMapId(239080724);
            Assert.NotEqual(239077654L, arena);
            var info = MapManager.GetMapInfo(arena);
            Assert.Equal(1131, info.SubAreaId);
            Assert.Equal(MapManager.RaidArenaFlags, info.Flags);
            Assert.Equal(239077652L, MapManager.ResolveArenaMapId(239077652));
            Assert.Equal(1131, MapManager.GetMapInfo(GuildRaidManager.GigalodonArena).SubAreaId);
        }

        /// <summary>
        /// The board is the sixteen fish of floor -3 [4,12]; lighting the last fish meets the goal
        /// that opens floor -4.
        /// </summary>
        [Fact]
        public async Task Lighting_the_Luminarium_opens_floor_four()
        {
            if (!HasWorld) return;
            var board = GuildRaidLuminarium.BoardOf(Raids.Of(Raids.Gigalodon)).Value;
            Assert.Equal((239081984L, 3), (board.MapId, board.Floor));
            Assert.Equal(new[] { 539818, 539821, 539822, 539823 }, board.Fish.Take(4).Select(f => f.Id));

            var raid = new RaidInstance(77, Raids.Gigalodon, 1, 7001, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
            raid.Add(7001);
            var lit = Enumerable.Repeat(true, 16).ToArray();
            GuildRaidLuminarium.Press(lit, 0);                        // one press from solved
            GuildRaidLuminarium.Set(raid, lit);
            Assert.Same(lit, GuildRaidLuminarium.Of(raid));
            Assert.Equal(GuildRaidLuminarium.Out, GuildRaidLuminarium.StateFor(raid, board.MapId, board.Fish[0].Id));

            Assert.True(await GuildRaidLuminarium.PressAsync(null, raid, board.MapId, board.Fish[0].Id, GuildRaidLuminarium.FishSkill));
            Assert.True(lit.All(l => l));
            Assert.Equal(1, raid.GoalAt(15));
            Assert.True(raid.IsOpen(4));
            Assert.False(await GuildRaidLuminarium.PressAsync(null, raid, 1L, board.Fish[0].Id, GuildRaidLuminarium.FishSkill));
        }

        /// <summary>
        /// The Santuario is walked through its hub, the castle's [15,17]: a portal to each zone, to its
        /// way back on the zone's most central map, and each way back to the hub. The raid starts there.
        /// </summary>
        [Fact]
        public void The_santuario_s_zones_are_joined_through_its_hub()
        {
            if (!HasWorld) return;
            var gardens = Raids.Of(Raids.EternalGardens);
            var hub = GuildRaidPassages.HubOf(gardens).Value;
            Assert.Equal(238296325L, hub.MapId);
            Assert.Equal(238296325L, GuildRaidManager.EntryMapOf(gardens));
            Assert.Null(GuildRaidPassages.HubOf(Raids.Of(Raids.Gigalodon)));

            var routes = GuildRaidPassages.Derive();
            long Into(int zone) => routes.Single(r => r.SourceMapId == hub.MapId && r.ElementId == hub.Portals[zone].Id).DestinationMapId;
            Assert.Equal(238294274L, Into(1));      // Obra [22,8]
            Assert.Equal(238297345L, Into(2));      // Enclave [11,20]
            Assert.Equal(238299905L, Into(3));      // Reserva [20,16]
            Assert.Equal(238302465L, Into(4));      // Patio [10,14]

            var back = routes.Single(r => r.SourceMapId == 238297857L && r.GfxId == GuildRaidPassages.ReturnPortalGfx);
            Assert.Equal((hub.MapId, hub.Portals[2].Cell), (back.DestinationMapId, back.DestinationCellId));
            Assert.All(routes.Where(r => r.GfxId == GuildRaidPassages.ReturnPortalGfx || GuildRaidPassages.ZonePortalGfx.ContainsKey(r.GfxId)),
                       r => Assert.Equal((GuildRaidPassages.PortalType, TeleportManager.UseSkill), (r.InteractiveType, r.SkillId)));
        }

        /// <summary>
        /// The four guardians sleep until the four enigmas are solved -- what their goals wait for in
        /// the client's data --, then wake in the middle of their zones, where the hub's portals
        /// arrive, by their statues: each with one of each kind beside it, the Centinela with its
        /// obelisks besides; the statues go and the hub shows their logos. Each one beaten ends its
        /// zone and takes its logo away; the fourth dissolves the hub's door.
        /// </summary>
        [Fact]
        public async Task The_guardians_wake_by_their_statues_once_the_enigmas_are_solved()
        {
            if (!HasWorld) return;
            if (!MapManager.WalkableCells.ContainsKey(238294274)) MapManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            var gardens = Raids.Of(Raids.EternalGardens);
            long Centre(int zone) => GuildRaidPassages.ArrivalOf(gardens, zone).Value.MapId;
            long hub = GuildRaidPassages.HubOf(gardens).Value.MapId;
            bool Stands(RaidInstance r, long map, string name) => GuildRaidStagings.Standing(r, map).Contains(GuildRaidStagings.IdOf(map, name));
            bool Guarded(int zone) => MobSpawnManager.GetMobsForMap(Centre(zone)).Any(g => g.Members[0].Monster.Id == GuildRaidGuardians.GuardianOfZone[zone]);
            foreach (var zone in GuildRaidGuardians.GuardianOfZone.Keys) MobSpawnManager.RestoreMap(Centre(zone));

            var raid = new RaidInstance(79, Raids.EternalGardens, 1, 7001, DateTimeOffset.UtcNow, TimeSpan.FromHours(2));
            raid.SetHealth(gardens.Health);
            try
            {
                Assert.Equal(new[] { 1, 2, 3, 4 }, GuildRaidGuardians.WaitedFor(Raids.EternalGardens));
                Assert.All(GuildRaidGuardians.GuardianOfZone.Keys, z => Assert.False(Guarded(z)));
                Assert.Equal(238294274L, Centre(1));

                // Three enigmas solved: they still sleep.
                var goals = GuildRaidCatalogue.GoalsOf(Raids.EternalGardens);
                async Task Meet(int goal) => await GuildRaidManager.AdvanceGoalAsync(raid, goal, Math.Max(1, goals.Single(g => g.Id == goal).Value));
                foreach (int enigma in new[] { 1, 2, 3 }) await Meet(enigma);
                Assert.False(GuildRaidGuardians.Due(raid));
                Assert.All(GuildRaidGuardians.GuardianOfZone.Keys, z => Assert.False(Guarded(z)));

                // The fourth: all four wake, by their statues.
                await Meet(4);
                Assert.Equal(1, raid.Get(GuildRaidGuardians.AwakeVariable));
                foreach (var (zone, guardian) in GuildRaidGuardians.GuardianOfZone)
                {
                    var group = MobSpawnManager.GetMobsForMap(Centre(zone)).Single(g => g.Members[0].Monster.Id == guardian);
                    Assert.Equal(GuildRaidGuardians.GroupOf(guardian), group.Members.Select(m => m.Monster.Id));
                    Assert.Equal(GuildRaidGuardians.CellOf(Centre(zone)), group.CellId);
                    Assert.True(Stands(raid, Centre(zone), GuildRaidGuardians.Wakes));
                    Assert.True(Stands(raid, hub, GuildRaidGuardians.HubLogo(zone)));
                }
                Assert.All(GuildRaidGuardians.GuardianOfZone.Keys,
                           z => Assert.Contains(GuildRaidGuardians.CellOf(Centre(z)), MapManager.WalkableCells[Centre(z)]));

                // The Vigilante de la Obra beaten: its zone is done, its logo gone; the door stands.
                await Meet(GuildRaidGuardians.GoalOf(Raids.EternalGardens, GuildRaidGuardians.Vigilante).Id);
                Assert.True(Stands(raid, Centre(1), GuildRaidGuardians.AuraEnds));
                Assert.True(Stands(raid, Centre(1), GuildRaidGuardians.ZoneDone));
                Assert.False(Stands(raid, hub, GuildRaidGuardians.HubLogo(1)));
                Assert.True(Stands(raid, hub, GuildRaidGuardians.HubLogo(2)));
                Assert.False(Stands(raid, hub, GuildRaidGuardians.DoorDissolves));

                // The other three: the door dissolves.
                foreach (int guardian in new[] { GuildRaidGuardians.Guardian, GuildRaidGuardians.Centinela, 8319 })
                    await Meet(GuildRaidGuardians.GoalOf(Raids.EternalGardens, guardian).Id);
                Assert.True(Stands(raid, hub, GuildRaidGuardians.DoorDissolves));
                Assert.DoesNotContain(GuildRaidGuardians.GuardianOfZone.Keys, z => Stands(raid, hub, GuildRaidGuardians.HubLogo(z)));
            }
            finally
            {
                GuildRaidStagings.Forget(raid);
                foreach (var zone in GuildRaidGuardians.GuardianOfZone.Keys) MobSpawnManager.RestoreMap(Centre(zone));
            }
        }

        /// <summary>
        /// The Cangrancio's statues: the four of floor -4 [9,12], worked in the order its fight showed
        /// its forms. Before the order is known they do nothing; a mistake costs 1000 and starts over;
        /// the fourth right meets goal 16 and opens floor -5.
        /// </summary>
        [Fact]
        public async Task The_statues_open_floor_five_in_the_forms_order()
        {
            if (!HasWorld) return;
            var statues = GuildRaidExecrabe.StatuesOf(Raids.Of(Raids.Gigalodon)).Value;
            Assert.Equal(239083016L, statues.MapId);
            Assert.Equal(GuildRaidExecrabe.Forms, statues.Statues.Select(s => s.Form).OrderBy(f => f));
            int StatueOf(int form) => statues.Statues.Single(s => s.Form == form).Statue.Id;

            var raid = new RaidInstance(78, Raids.Gigalodon, 1, 7001, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
            raid.Add(7001);
            Task<bool> Press(int form) => GuildRaidExecrabe.PressAsync(null, raid, statues.MapId, StatueOf(form), GuildRaidExecrabe.StatueSkill);

            Assert.True(await Press(6724));                     // nothing known yet: nothing happens
            Assert.Equal(0, raid.Score);

            var order = new[] { 6726, 6724, 6727, 6725 };
            raid.SetSequence(GuildRaidExecrabe.OrderSequence, order);
            Assert.Equal(GuildRaidExecrabe.Lit, GuildRaidExecrabe.StateFor(raid, 6725));

            Assert.True(await Press(6726));
            Assert.True(await Press(6727));                     // out of order
            Assert.Equal(-GuildRaidExecrabe.MistakeCost, raid.Score);
            Assert.Equal(0, raid.Get(GuildRaidExecrabe.ProgressVariable));

            foreach (int form in order) Assert.True(await Press(form));
            Assert.Equal(1, raid.GoalAt(16));
            Assert.True(raid.IsOpen(5));
            Assert.False(await GuildRaidExecrabe.PressAsync(null, raid, 1L, StatueOf(6724), GuildRaidExecrabe.StatueSkill));
        }

        /// <summary>
        /// The salt deposits are the 34 crystals of graphic 6943 on floors -1 to -5, declared with the
        /// client's type "Sal de las profundidades" and skill "Recolectar sal de las profundidades",
        /// which gathers item 32464 at job 1; they grow back in five to ten minutes.
        /// </summary>
        [Fact]
        public void The_salt_deposits_are_the_abyss_s_crystals()
        {
            if (!HasWorld) return;
            var deposits = GuildRaidSaltDeposits.Read();
            Assert.Equal(34, deposits.Count);
            var abyss = Raids.Of(Raids.Gigalodon);
            Assert.All(deposits, d =>
            {
                Assert.Equal((464, 470, 32464, 1), (d.Type, d.SkillId, d.ItemId, d.JobId));
                Assert.InRange(abyss.FloorOf(DatabaseManager.SubAreaOfMap(d.MapId)), 1, 5);
                Assert.True(GuildRaidSaltDeposits.IsDeposit(d));
            });

            var dice = new Random(3);
            for (int i = 0; i < 100; i++)
                Assert.InRange(GuildRaidSaltDeposits.Regrowth(dice), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        }
    }
}
