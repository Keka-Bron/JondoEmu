using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Where the luminomachines stand, and what happens when salt goes into one.
    ///
    /// One machine per floor of the Abyss that has light — five floors, five machines, five
    /// <c>nX_worldlight</c> variables — and the light it buys is the raid instance's, so a machine
    /// only does anything for someone who is actually inside a running raid. Outside one there is
    /// no instance to light, and the machine says nothing it could not deliver.
    /// </summary>
    /// <remarks>
    /// The rules of the thing are in <see cref="Luminomachine"/>, read off the client's data. What
    /// is here is the two halves that need a server: where the machines are, and the inventory.
    /// </remarks>
    public static class Luminomachines
    {
        /// <summary>A machine, on the map it was put on.</summary>
        public readonly record struct Placement(int Floor, int SubArea, long MapId, int Cell);

        private static readonly List<Placement> _placed = new();

        /// <summary>The machines, in floor order.</summary>
        public static IReadOnlyList<Placement> Placed => _placed;

        /// <summary>
        /// Works out where the five machines go and remembers it.
        /// </summary>
        /// <remarks>
        /// NOT MEASURED: no capture goes into a raid and the Abyss carries no NPC placement in the
        /// client's data. But its maps draw the machine where each floor is reached -- in the
        /// outpost, beside the lift's cage on floor -2 -- so each machine stands on the map its floor
        /// is reached by (GuildRaidPassages.ArrivalMapOf): the outpost, then where the way down from
        /// the floor above arrives; on the walkable cell nearest the middle of it. It used to be the
        /// floor's lowest map by number, which on floor -1 is a fight arena nobody walks.
        ///
        /// The sixth floor of the Abyss gets none: it has no light variable of its own, which is
        /// the data's way of saying the boss's floor is not lit with salt.
        /// </remarks>
        public static void Place()
        {
            _placed.Clear();

            var abyss = Raids.Of(Raids.Gigalodon);
            if (abyss == null || !abyss.HasLight) return;

            var routes = GuildRaidPassages.Derive();
            for (int floor = 1; floor <= Luminomachine.Machines && floor <= abyss.Floors.Count; floor++)
            {
                int subArea = abyss.Floors[floor - 1];
                var maps = DatabaseManager.MapsOfSubArea(subArea);
                if (maps.Count == 0) continue;

                long mapId = GuildRaidPassages.ArrivalMapOf(abyss, floor, routes);
                if (mapId == 0) mapId = maps.Min();
                int cell = MapManager.GetNearestWalkableCell(mapId, Handlers.TeleportHandler.MapCentre);
                if (cell < 0) continue;

                _placed.Add(new Placement(floor, subArea, mapId, cell));
            }

            Console.WriteLine($"[Luminomachines] {_placed.Count} placed, one per floor with light.");
        }

        /// <summary>The floor whose machine stands on this map, or zero if none does.</summary>
        public static int FloorOn(long mapId)
        {
            foreach (var placement in _placed)
            {
                if (placement.MapId == mapId) return placement.Floor;
            }

            return 0;
        }

        /// <summary>
        /// How lit that floor is for this character: his raid's own number, and nothing when he is
        /// not in one.
        /// </summary>
        public static int LightOn(long characterId, int floor)
        {
            var raid = GuildRaidManager.RaidOf(characterId);
            if (raid == null) return -1;
            return Math.Clamp(raid.LightAt(floor, DateTimeOffset.UtcNow), 0, Luminomachine.MostLight);
        }

        /// <summary>
        /// Puts the salt in: raises the floor's light and returns how bright it left it, or -1 when
        /// the deposit could not be made.
        /// </summary>
        /// <remarks>
        /// The salt is taken by whoever pays, and the light belongs to the raid, so the whole team
        /// sees the floor brighten off one player's bag. That is the point of the thing.
        /// </remarks>
        public static int Deposit(long characterId, int floor, int from, int to)
        {
            var raid = GuildRaidManager.RaidOf(characterId);
            if (raid == null) return -1;

            var now = DateTimeOffset.UtcNow;
            if (raid.LightAt(floor, now) != from) return -1;
            if (to <= from || to > Luminomachine.MostLight) return -1;

            raid.SetLight(floor, to, now);
            Console.WriteLine($"[Luminomachines] Floor {floor} from {from} to {to} bands of light, " +
                              $"{Luminomachine.Cost(from, to)} salts from {characterId}.");
            return to;
        }
    }
}
