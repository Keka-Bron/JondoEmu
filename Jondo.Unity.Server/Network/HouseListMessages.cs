using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal static class HouseListMessages
{
    internal static HouseAccountEvent Account(long accountId)
        => new(HouseStore.OwnedBy(accountId).Select(house => Listed(house, guild: false)).OfType<House>().ToArray());

    internal static GuildHousesEvent Guild(long guildId)
        => new(HouseStore.SharedWith(guildId).Select(house => Listed(house, guild: true)).OfType<House>().ToArray());

    private static House? Listed(HouseStore.House house, bool guild)
    {
        if (!Houses.TryGetDoor(house.MapId, house.ElementId, out var door)) return null;
        var street = MapManager.GetMapInfo(house.MapId);
        var coordinates = new MapExtendedCoordinates(house.MapId, street?.PosX ?? 0, street?.SubAreaId ?? 0, street?.PosY ?? 0);
        // List plaques deliberately exclude the nested guild, even for shared houses.
        var instance = MapHouseMessages.Instance(house, door, full: true, withGuild: false);
        return new(Houses.HouseIdOf(house.MapId, house.ElementId), door.Model, guild
            ? new HouseInformationCaseHouseGuild(new(coordinates, instance, house.SharedWithGuild ? house.GuildRights : 0))
            : new HouseInformationCaseHouseAccount(new(coordinates, instance)));
    }
}
