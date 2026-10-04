using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Where each raid's chest stands, what goes into it and what taking it does.
    /// </summary>
    /// <remarks>
    /// The chest is the raid's ending and its scoreboard at once: treasures go in and the score
    /// goes up, and whoever takes it ends the run for everybody. The rules of what it says are in
    /// <see cref="RaidChest"/>; what is here is the half that needs a server.
    /// </remarks>
    public static class RaidChests
    {
        /// <summary>A chest, on the map it was put on.</summary>
        public readonly record struct Placement(int RaidId, int SubArea, long MapId, int Cell);

        private static readonly List<Placement> _placed = new();

        /// <summary>The chests, one per raid.</summary>
        public static IReadOnlyList<Placement> Placed => _placed;

        /// <summary>
        /// Works out where each raid's chest goes.
        /// </summary>
        /// <remarks>
        /// NOT MEASURED, like everything else about where things stand in a raid. The rule is the
        /// far end of the descent: the LAST floor of the raid, its lowest map by number, and the
        /// walkable cell nearest the middle. For the Abyss that is the Fosombrío de Willorca, the
        /// sixth floor -- the one with no light of its own, which is the data's way of saying it is
        /// not a floor you light, it is the one you end on -- and for the Sanctuary the Castillo
        /// del santuario. One line to change the day somebody measures it.
        /// </remarks>
        public static void Place()
        {
            _placed.Clear();

            foreach (var kind in Raids.All)
            {
                if (kind.Floors.Count == 0) continue;
                int subArea = kind.Floors[^1];

                var maps = DatabaseManager.MapsOfSubArea(subArea);
                if (maps.Count == 0) continue;

                long mapId = maps.Min();
                int cell = MapManager.GetNearestWalkableCell(mapId, Handlers.TeleportHandler.MapCentre);
                _placed.Add(new Placement(kind.Id, subArea, mapId, cell));
            }

            Console.WriteLine($"[Raids] {_placed.Count} cofres puestos, uno al final de cada raid.");
        }

        /// <summary>Which raid's chest stands on this map, or zero when none does.</summary>
        public static int RaidOn(long mapId)
        {
            foreach (var chest in _placed)
            {
                if (chest.MapId == mapId) return chest.RaidId;
            }

            return 0;
        }

        /// <summary>The score of the raid this character is in, or -1 when he is in none.</summary>
        public static long ScoreOf(long characterId)
        {
            var raid = GuildRaidManager.RaidOf(characterId);
            return raid == null ? -1 : raid.Score;
        }

        /// <summary>
        /// Empties a bagful of treasures into the chest and returns the score it left, or -1 when
        /// there was no raid to put them in.
        /// </summary>
        /// <remarks>
        /// The caller takes the items out of the bag; this only moves the number. Split that way on
        /// purpose: taking items can fail halfway -- a stack that went somewhere else between the
        /// window opening and the answer arriving -- and the score must count what actually left
        /// the bag, not what was offered.
        /// </remarks>
        public static long Drop(long characterId, IReadOnlyDictionary<int, int> dropped)
        {
            var raid = GuildRaidManager.RaidOf(characterId);
            if (raid == null) return -1;

            long worth = RaidTreasures.Worth(dropped);
            long now = raid.Add(RaidInstance.ScoreVariable, worth);

            Console.WriteLine($"[Raids] El {characterId} suelta {dropped.Count} clases de tesoro " +
                              $"por {worth} puntos; el cofre va por {now}.");
            return now;
        }

        /// <summary>
        /// Takes the chest: the raid ends for everybody, with the score it had.
        /// </summary>
        public static async Task<long> TakeAsync(long characterId)
        {
            var raid = GuildRaidManager.RaidOf(characterId);
            if (raid == null) return -1;

            long score = raid.Score;
            await GuildRaidManager.FinishAsync(raid, RaidInstance.Ending.Beaten);
            return score;
        }

        /// <summary>For the tests: forget where they were.</summary>
        internal static void Forget() => _placed.Clear();
    }
}
