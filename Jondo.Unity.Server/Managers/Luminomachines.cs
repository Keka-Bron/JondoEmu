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
        /// NOT MEASURED, and it cannot be: no capture goes into a raid, the Abyss carries no NPC
        /// placement in the client's data and none of its seventy-three maps has an interactive
        /// either, so there is nothing to point at and say "the machine was here". The rule chosen
        /// is the same one the raid entrance uses -- the lowest map of the floor, by number -- on
        /// the walkable cell nearest the middle of it. It is deterministic, it is reachable, and
        /// it is one line to change the day somebody measures it.
        ///
        /// The sixth floor of the Abyss gets none: it has no light variable of its own, which is
        /// the data's way of saying the boss's floor is not lit with salt.
        /// </remarks>
        public static void Place()
        {
            _placed.Clear();

            var abyss = Raids.Of(Raids.Gigalodon);
            if (abyss == null || !abyss.HasLight) return;

            for (int floor = 1; floor <= Luminomachine.Machines && floor <= abyss.Floors.Count; floor++)
            {
                int subArea = abyss.Floors[floor - 1];
                var maps = DatabaseManager.MapsOfSubArea(subArea);
                if (maps.Count == 0) continue;

                long mapId = maps.Min();
                int cell = MapManager.GetNearestWalkableCell(mapId, Handlers.TeleportHandler.MapCentre);
                if (cell < 0) continue;

                _placed.Add(new Placement(floor, subArea, mapId, cell));
            }

            Console.WriteLine($"[Luminomáquinas] {_placed.Count} puestas, una por planta con luz.");
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
            return (int)Math.Clamp(raid.Get(RaidInstance.LightVariable(floor)), 0, Luminomachine.MostLight);
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

            string variable = RaidInstance.LightVariable(floor);
            if (raid.Get(variable) != from) return -1;
            if (to <= from || to > Luminomachine.MostLight) return -1;

            raid.Set(variable, to);
            Console.WriteLine($"[Luminomáquinas] Planta {floor} de {from} a {to} franjas de luz, " +
                              $"{Luminomachine.Cost(from, to)} sales del {characterId}.");
            return to;
        }
    }
}
