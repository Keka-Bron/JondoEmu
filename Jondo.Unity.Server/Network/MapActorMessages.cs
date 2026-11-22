using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Resolver = Jondo.Unity.World.Content.Criterion.Resolver;

namespace Jondo.Unity.Server.Network;

/// <summary>Shared roleplay actor snapshots for map contents and presence updates.</summary>
internal static class MapActorMessages
{
    internal static GameRolePlayShowActorsEvent Refreshed(DbCharacter character, int cell, int facing, long accountId)
        => new([Player(character, cell, facing, accountId)]);

    internal static ActorPositionInformation Player(DbCharacter character, int cell, int facing, long accountId)
    {
        var mutation = Mutations.OfCharacter(character.Id);
        var alignment = Alignments.Of(character.Id);
        return Player(character, cell, facing, accountId, CharacterLookBuilder.SemanticMapLook(character),
            mutation == null ? new(0, Side(alignment.Side), alignment.PvpOn
                ? new(0, AttackableStatus.PvpEnabledAttackable, 0, 0) : null,
                alignment.ShownGrade, CharacteristicRules.StatLevel(character.Level)) : null,
            mutation == null ? null : new(mutation.Grade, mutation.Monster), Options(character.Id, character.Level));
    }

    internal static ActorPositionInformation Player(DbCharacter character, int cell, int facing, long accountId,
        EntityLook look, AlignmentInformation? alignment, MutantInformation? mutation, IReadOnlyList<HumanOption> options)
        => Roleplay(character.Id, cell, facing, look, new RolePlayActorCaseNamedActor(new(character.Name,
            new NamedActorCaseHumanoidInformation(new(alignment, mutation, accountId, options.ToArray(),
                Hierarchy.RolePlayer, Handlers.OverloadRestriction.RememberActorOverload(character.Id)
                    ? [Restriction.CannotRun, Restriction.CannotAttackAsMutant]
                    : [Restriction.CannotAttackAsMutant], Gender.Male)))));

    private static HumanOption[] Options(long characterId, int level)
    {
        var options = new List<HumanOption>();
        int aura = Wardrobe.ShownBy(characterId);
        if (aura != Wardrobe.None) options.Add(new(new HumanOptionCaseEmoteOption(new(aura))));
        if (GuildStore.GuildOf(characterId) is { } guild)
            options.Add(new(new HumanOptionCaseGuildInformation(GuildStateMessages.Information(guild))));
        if (AllianceStore.AllianceOf(characterId) is { } alliance)
            options.Add(new(new HumanOptionCaseAllianceInformation(new(AvaStatus(AllianceProtocol.AggressableStatus(characterId)), Alliance(alliance)))));
        var followers = SlotLooks.FollowerLooks(SlotLooks.Of(characterId));
        if (followers.Count > 0)
            options.Add(new(new HumanOptionCaseFollowers(new(followers.Select((look, index) =>
                new HumanIndexedEntityLook(MonsterMessages.Look(look, false), index)).ToArray()))));
        var (title, ornament) = Wardrobe.Of(characterId);
        if (title != Wardrobe.None) options.Add(new(new HumanOptionCaseTitleOption(new("", title))));
        if (ornament != Wardrobe.None) options.Add(new(new HumanOptionCaseOrnamentOption(new(level, null, null, ornament))));
        options.Add(new(new HumanOptionCaseSpeedMultiplier(1)));
        return options.ToArray();
    }

    internal static Alignment Side(int side) => side switch
    {
        0 => Alignment.Unknown, 1 => Alignment.Without, Alignments.Neutral => Alignment.Neutral,
        Alignments.Bonta => Alignment.Angel, Alignments.Brakmar => Alignment.Evil,
        _ => throw new InvalidOperationException($"Unsupported map actor alignment {side}."),
    };

    internal static Direction Facing(int facing) => facing switch
    {
        0 => Direction.East, 1 => Direction.SouthEast, 2 => Direction.South, 3 => Direction.SouthWest,
        4 => Direction.West, 5 => Direction.NorthWest, 6 => Direction.North, 7 => Direction.NorthEast,
        _ => throw new InvalidOperationException($"Unsupported map actor direction {facing}."),
    };

    internal static AttackableStatus AvaStatus(int status) => status switch
    {
        AllianceProtocol.NotAggressable => AttackableStatus.NonAttackable,
        AllianceProtocol.AvaAggressable => AttackableStatus.AvaEnabledAttackable,
        AllianceProtocol.AvaNotAggressable => AttackableStatus.AvaEnabledNonAttackable,
        _ => throw new InvalidOperationException($"Unsupported map actor AvA status {status}."),
    };

    // The storage's historical EmblemSymbolColor column is the reviewed background
    // shape (tag 2). Tag 4 is unmapped and is deliberately not reproduced.
    internal static AllianceInformation Alliance(AllianceStore.Alliance alliance)
        => new(alliance.Name, new(alliance.EmblemSymbol, alliance.EmblemSymbolColor,
            alliance.EmblemBackground, alliance.EmblemSymbolRgb), alliance.Uuid, alliance.Tag, null);

    internal static ActorPositionInformation Roleplay(long id, int cell, int facing, EntityLook look, RolePlayActorCase identity)
        => new(new(cell, Facing(facing), null, null), new(look, new ActorInformationCaseRolePlayActor(new(identity))), id);

    internal static ActorPositionInformation MonsterGroup(MobSpawnManager.MobGroup group)
        => MonsterGroup(group, ServerSettings.Current.Hardcore);

    internal static ActorPositionInformation MonsterGroup(MobSpawnManager.MobGroup group, bool hardcore)
    {
        MonsterInGroupInformation Member(MobSpawnManager.MobMember member, bool withLook)
            => new(member.Monster.Id, MonsterMessages.LevelOf(member),
                withLook ? MonsterMessages.Look(member.Monster.Look, hardcore) : null,
                MonsterMessages.GradeOf(member));
        if (group.Members.Count == 0) throw new ArgumentException("A map monster group needs a leader.", nameof(group));
        var members = group.Members.ToArray();
        // One group actor, with exactly one leader. Its look belongs to the actor;
        // only underlings have individual looks, never leaders or alternatives.
        var alternatives = group.Modular
            ? Enumerable.Range(MobSpawnManager.DungeonMinimum,
                Math.Max(0, Math.Min(members.Length, MobSpawnManager.DungeonGroupSize) - MobSpawnManager.DungeonMinimum + 1))
                .DistinctBy(size => Math.Max(size, Math.Min(group.Leaders, members.Length)))
                .Select(size => new MonstersInGroupAlternativeInformation(MobSpawnManager.MembersFor(group, size).Select(member => Member(member, false)).ToArray(),
                    size == MobSpawnManager.DungeonMinimum ? 1 : size)).ToArray()
            : [];
        return Roleplay(group.MobId, group.CellId, group.Orientation,
            MonsterMessages.Look(members[0].Monster.Look, hardcore),
            new RolePlayActorCaseMonsterGroupActor(new(Alignment.Without,
                new(members.Skip(1).Select(member => Member(member, true)).ToArray(), Member(members[0], false), alternatives), null, -1)));
    }

    internal static EntityLook NpcLook(long bones, IEnumerable<long> skins, IEnumerable<long> colors, IEnumerable<long> scales)
        => new(colors.Select(value => checked((int)value)).ToArray(), 3, checked((int)bones),
            scales.Select(value => checked((int)value)).ToArray(), skins.Select(value => checked((int)value)).ToArray(), []);

    internal static GameRolePlayShowActorsEvent NpcShown(Npcs.Spawn npc, long mapId)
    {
        Resolver? resolver = null;
        return new([Npc(npc, mapId, ref resolver)]);
    }

    internal static ActorPositionInformation Npc(Npcs.Spawn npc, long mapId, ref Resolver? resolver)
    {
        var variant = npc.Variants.Count > 1
            ? Npcs.VariantFor(npc, resolver ??= WorldEvents.Answering(GuildRaidManager.ResolverFor(GameState.CharacterId), DateTimeOffset.UtcNow))
            : null;
        var look = NpcLook(variant?.Bones ?? npc.Bones, variant?.Skins ?? npc.Skins, variant?.Colors ?? npc.Colors, variant?.Scales ?? npc.Scales);
        var offered = Quests.OfferedRightNowBy(npc.NpcId, mapId).ToArray();
        var doing = Quests.InProgressWith(npc.NpcId, mapId).ToArray();
        var template = Npcs.TemplateOf(npc.NpcId);
        // Only template gender 1 is transmitted as female; other values retain
        // the capture's omitted/male default. No quests means absent, not empty.
        return Roleplay(npc.ContextualId, npc.Cell, npc.Orientation, look,
            new RolePlayActorCaseNpcStaticInformation(new("", doing.Length == 0 && offered.Length == 0 ? null : new(doing, offered),
                template?.Gender == 1 ? Gender.Female : Gender.Male, 0, npc.NpcId, [])));
    }

    internal static GameRolePlayShowActorsEvent TaxCollectorShown(TaxCollectorStore.TaxCollector collector, AllianceStore.Alliance alliance)
        => new([TaxCollector(collector, alliance)]);

    internal static ActorPositionInformation TaxCollector(TaxCollectorStore.TaxCollector collector, AllianceStore.Alliance alliance)
        // Keep the last-name and first-name catalogue IDs distinct: swapping them
        // leaves some collectors without the tooltip/menu's name lookup.
        => Roleplay(collector.ContextId, collector.CellId, collector.Direction,
            MonsterMessages.Look(TaxCollectorProtocol.LookString(alliance, TaxCollectorProtocol.MapBones), false),
            new RolePlayActorCaseTaxCollectorActor(new(new(Alliance(alliance), collector.LastNameId, collector.Uuid,
                collector.OwnerId, collector.FirstNameId), 0)));

    internal static GameRolePlayShowActorsEvent PrismShown(PrismStore.Prism prism, AllianceStore.Alliance alliance)
        => new([Prism(prism, alliance)]);

    internal static ActorPositionInformation Prism(PrismStore.Prism prism, AllianceStore.Alliance alliance)
        => Roleplay(prism.ContextId, prism.CellId, prism.Direction,
            MonsterMessages.Look(PrismProtocol.LookString(alliance), false),
            new RolePlayActorCasePrismActor(PrismMessages.Live.State(prism, alliance)));

    internal static ActorPositionInformation? Portal(DimensionPortals.Portal portal)
    {
        var template = Npcs.EnsureTemplate(portal.Dimension.PortalNpc);
        if (template == null) return null;
        var variant = Npcs.Variantes(template.Look).FirstOrDefault();
        var look = variant == null ? NpcLook(0, [], [], []) : NpcLook(variant.Bones, variant.Skins, variant.Colors, variant.Scales);
        int area = Math.Max(0, Interactives.AreaOfSubArea(MapManager.GetMapInfo(portal.Dimension.EntryMap)?.SubAreaId ?? 0));
        return Roleplay(portal.ContextId, portal.Cell, DimensionPortals.Facing, look, new RolePlayActorCasePortalActor(new(portal.Type, area)));
    }
}
