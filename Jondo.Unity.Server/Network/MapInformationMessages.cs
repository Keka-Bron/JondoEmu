using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IMapInformationSource
{
    IReadOnlyList<ActorPositionInformation> Actors(GameSession session, DbCharacter character);
    int SubAreaOf(long mapId);
    IReadOnlyList<MapObstacle> Obstacles(long mapId);
    IReadOnlyList<House> Houses(long mapId);
    (InteractiveElement[] Elements, StatedElement[] States) Interactives(GameSession session);
    IReadOnlyList<FightCommonInformation> Fights(long mapId);
    MapSpecificComplementaryInformationCase Specific(GameSession session, DbCharacter character);
}

/// <summary>Direct, viewer-owned complementary map information; no serialization or projection.</summary>
internal sealed class MapInformationMessages(IMapInformationSource source)
{
    internal static MapInformationMessages Live { get; } = new(new LiveSource());

    internal Task SendAsync(GameSession session, DbCharacter character) => session.SendAsync(Snapshot(session, character));

    internal MapComplementaryInformationEvent Snapshot(GameSession session, DbCharacter character)
    {
        using var context = SessionContext.Push(session);
        long mapId = session.MapId;
        var specific = source.Specific(session, character);
        var actors = source.Actors(session, character).ToArray();
        int subArea = source.SubAreaOf(mapId);
        var houses = source.Houses(mapId).ToArray();
        var (interactives, states) = source.Interactives(session);
        var obstacles = source.Obstacles(mapId).ToArray();
        var fights = source.Fights(mapId).ToArray();
        return new(mapId, actors, subArea, obstacles, houses, [], interactives.ToArray(), fights, states.ToArray(), specific);
    }

    internal static GameSession[] OtherPlayers(GameSession session, long characterId)
    {
        var scope = MapAudienceScope.Capture(session.State);
        return SessionRegistry.All().Where(other => scope.Contains(other) && other.CharacterId > 0
            && other.CharacterId != characterId && SessionRegistry.StandsOnTheMap(other)).ToArray();
    }

    private sealed class LiveSource : IMapInformationSource
    {
        public IReadOnlyList<ActorPositionInformation> Actors(GameSession session, DbCharacter character)
        {
            var actors = new List<ActorPositionInformation>
            {
                MapActorMessages.Player(character, session.State.CellId, session.State.Orientation, session.AccountId),
            };
            foreach (var other in OtherPlayers(session, character.Id))
            {
                if (DatabaseManager.GetCharacterById(other.CharacterId) is { } player)
                    actors.Add(MapActorMessages.Player(player, other.State.CellId, other.State.Orientation, other.AccountId));
            }
            foreach (var companion in CompanionPresence.On(session.MapId))
                if (companion.Character.Id != character.Id)
                    actors.Add(MapActorMessages.Player(companion.Character, companion.Cell, companion.Facing, companion.AccountId));
            foreach (var group in MobSpawnManager.GetMobsForMap(session.MapId))
                if (group.Members.Count > 0 && !FightHandler.IsGroupFighting(group.MobId)) actors.Add(MapActorMessages.MonsterGroup(group));
            Jondo.Unity.World.Content.Criterion.Resolver? resolver = null;
            foreach (var npc in Npcs.Of(session.MapId))
                if (Npcs.VisibleNow(npc)) actors.Add(MapActorMessages.Npc(npc, session.MapId, ref resolver));
            if (TaxCollectorStore.OnMap(session.MapId) is { } collector && !FightHandler.IsGroupFighting(collector.ContextId)
                && AllianceStore.ById(collector.AllianceId) is { } collectorAlliance)
                actors.Add(MapActorMessages.TaxCollector(collector, collectorAlliance));
            if (PrismStore.OnMap(session.MapId) is { } prism && !FightHandler.IsGroupFighting(prism.ContextId)
                && AllianceStore.ById(prism.AllianceId) is { } prismAlliance)
                actors.Add(MapActorMessages.Prism(prism, prismAlliance));
            foreach (var portal in DimensionPortals.OnMap(session.MapId))
                if (MapActorMessages.Portal(portal) is { } actor) actors.Add(actor);
            return actors.ToArray();
        }

        // The database subarea drives the client's map name, coordinates and minimap
        // marker; a fabricated zero on a known map breaks MapInfoUI initialization.
        public int SubAreaOf(long mapId) => MapManager.GetMapInfo(mapId)?.SubAreaId ?? 0;
        public IReadOnlyList<MapObstacle> Obstacles(long mapId)
            => PlateGates.On(mapId) is { } gate && PlateGates.IsOpen(mapId)
                ? gate.Obstacles.Select(cell => new MapObstacle(cell, MapObstacleState.ObstacleOpened)).ToArray() : [];
        public IReadOnlyList<House> Houses(long mapId) => MapHouseMessages.OnMap(mapId);
        public (InteractiveElement[] Elements, StatedElement[] States) Interactives(GameSession session)
            => MapInteractiveMessages.Snapshot(session.MapId, session.AccountId);
        public IReadOnlyList<FightCommonInformation> Fights(long mapId)
            => FightHandler.PlacementFightsOnMap(mapId).Select(FightHandler.MapSnapshotOf).ToArray();

        public MapSpecificComplementaryInformationCase Specific(GameSession session, DbCharacter character)
        {
            if (Merkasako.IsHavenBag(session.MapId))
            {
                long visited = session.State.HavenBagOwnerId;
                var owner = visited != 0 && visited != character.Id ? DatabaseManager.GetCharacterById(visited) ?? character : character;
                return new MapSpecificComplementaryInformationCaseHavenBagInformation(new(Merkasako.ThemeOfMap(session.MapId),
                    HavenBagStore.CurrentRoom(owner.Id), new(owner.Id,
                        new CharacterComplementCaseCharacterBasicInformation(new(owner.Name, owner.Level, new CharacterBasicInformationComplementCaseNone())))));
            }
            if (MapHouseMessages.Inside(session) is { } inside) return new MapSpecificComplementaryInformationCaseInHouseInformation(inside);
            if (GuildHalls.Visit(session.MapId, character.Id) is { } visit)
                return new MapSpecificComplementaryInformationCaseGuildInformation(new(visit.Own
                    ? new MapComplementaryGuildInformationCaseGuildHallInformation(new(new(visit.Theme, visit.Themes.ToArray())))
                    : new MapComplementaryGuildInformationCaseGuildHallSelectionInformation(new())));
            return new MapSpecificComplementaryInformationCaseNone();
        }
    }
}
