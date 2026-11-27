using System;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The authored content read again with the server running: the test harness's reload route and
    /// the staff's ".reload" command both come here. The clients stay connected; what a map shows
    /// changes on entering it again.
    /// </summary>
    public static class ContentReload
    {
        /// <summary>
        /// The NPCs' placements and conversations, the objectives' bindings and criteria, the fights
        /// replies start, the gathering and fight drops, the hand-written loot groups, the readable
        /// signs and the passages. Returns how long it took, in milliseconds.
        /// </summary>
        public static long Run()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Managers.NpcDialogues.Reload();
            Managers.Npcs.Initialize();
            Managers.DungeonDoor.Initialize();
            Managers.Expeditions.Initialize();
            Managers.Bankers.Initialize();
            Managers.Quests.ReloadContent();
            Managers.NpcFights.Forget();
            Managers.FollowerLifetimes.Forget();
            Handlers.NpcBarterHandler.Forget();
            Managers.MapStagings.Forget();
            Managers.PlateGates.Forget();
            Managers.DimensionDungeons.Forget();
            Managers.ClosedBorders.Forget();
            Managers.MeasuredArrivals.Forget();
            Managers.MeasuredArenas.Forget();
            Managers.HarvestDrops.Forget();
            Managers.QuestFightDrops.Forget();
            Managers.LootBoxes.Forget();
            Managers.LegendaryHunts.ForgetCompany();
            Managers.TokenShops.Initialize();
            Managers.NpcShops.Initialize();
            Managers.DungeonExitRooms.Forget();
            // The monster groups written by hand (content/monsters/groups.json), which a quest's
            // fights stand on: read at the start only, they waited for a restart.
            Managers.MobSpawnManager.ReloadWritten();
            // The yearly events' monsters (content/world/event_monsters.json): read once, they
            // waited for a restart.
            Managers.EventGuests.Forget();
            Managers.FightWinAlterations.Forget();
            Managers.IdolRules.Forget();
            Managers.IdolRules.Load();
            Managers.MonsterSummonCaps.Reload();
            Managers.MonsterStandoffs.Reload();
            Managers.MonsterSpellGates.Reload();
            Managers.MonsterLentSpells.Reload();
            Managers.MonsterPartyGrades.Reload();
            Managers.Readables.Forget();
            Managers.InteractiveMessages.Forget();
            Managers.FloorRiddles.Forget();
            Managers.InteractiveWays.Forget();
            Managers.TeleportManager.Initialize();
            // The stations written by hand in content/interactives/workshops.json, before the registry
            // that declares them is built again.
            Managers.Workshops.Initialize();
            // The passages are declared to the client from here: a door written since the start was
            // reloaded above and still answered "Unknown use" until this was built again.
            Managers.InteractiveRegistry.Initialize();
            Console.WriteLine($"[Content] Reloaded in {watch.ElapsedMilliseconds} ms.");
            return watch.ElapsedMilliseconds;
        }
    }
}
