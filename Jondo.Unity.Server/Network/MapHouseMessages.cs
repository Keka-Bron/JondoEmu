using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal static class MapHouseMessages
{
    internal static House[] OnMap(long mapId)
        => Houses.On(mapId).Where(door => door.IsOwnable).Select(door => new House(
            Houses.HouseIdOf(mapId, door.ElementId), door.Model,
            new HouseInformationCaseHouseOnMap(new([door.ElementId], HouseStore.CopiesOf(mapId, door.ElementId, door.Copies)
                .Select(house => Instance(house, door)).ToArray())))).ToArray();

    internal static MapComplementaryInHouseInformation? Inside(GameSession session)
    {
        if (!Houses.IsInterior(session.MapId)) return null;
        var (mapId, elementId, instance) = HouseHandler.HouseOf(session.State);
        if (mapId == 0 || !Houses.TryGetDoor(mapId, elementId, out var door) || !door.IsOwnable) return null;
        var house = HouseStore.Of(mapId, elementId, instance);
        if (!house.Owned) return null;
        var street = MapManager.GetMapInfo(mapId);
        return new(new(Houses.HouseIdOf(mapId, elementId), door.Model,
            new HouseInformationCaseHouseInside(new(new(street?.PosX ?? 0, street?.PosY ?? 0), Instance(house, door)))));
    }

    internal static HouseInstance Instance(HouseStore.House house, Houses.Door door, bool full = false, bool withGuild = true)
    {
        var (name, tag) = house.Owned ? HouseHandler.AccountTagOf(house.AccountId) : ("", "");
        GuildInformation? guild = null;
        // Match the existing plaque's optional social lookup failure policy.
        if (withGuild && house.SharedWithGuild)
        {
            try { if (GuildStore.ById(house.GuildId) is { } shared) guild = GuildStateMessages.Information(shared); }
            catch { }
        }
        var result = Instance(house.Instance, name, tag, HouseStore.AskingPrice(house, door.Price),
            house.Owned && house.Locked, guild);
        return !full ? result : result with
        {
            RoomsCount = door.Rooms,
            ChestsCount = Houses.ChestCountOf(door.InteriorMapId),
            SkillsId = Houses.PlaqueSkillsOf(door.InteriorMapId).Select(value => checked((int)value)).ToArray(),
        };
    }

    internal static HouseInstance Instance(int instance, string ownerName, string ownerTag, long price, bool adminLocked, GuildInformation? guild)
        // Preserve the short-plaque policy: tag 5 is the admin lock, separately
        // from tag 9's historical has-owner door-lock policy. Full room/skill data
        // belongs to the account/guild lists, not these map plaques.
        => new(false, guild, ownerName.Length > 0, adminLocked, 0, price == 0 ? null : price,
            new(ownerName, ownerTag), ownerName.Length > 0, 0, [], instance);
}
