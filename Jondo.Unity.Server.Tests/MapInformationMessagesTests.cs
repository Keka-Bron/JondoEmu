using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class MapInformationMessagesTests
{
    private static readonly EntityLook Look = new([0x01123456], 3, 73, [115], [91], []);

    [Fact]
    public void Player_snapshot_preserves_alignment_mutation_options_and_recursive_look_without_serialization()
    {
        var character = new DbCharacter { Id = 9_000_000_002, Name = "Visitor", Level = 253 };
        var alignment = new AlignmentInformation(7, Alignment.Evil, new(101, AttackableStatus.PvpEnabledAttackable, 203, 89), 4, 200);
        var mount = Look with { SubEntities = [new(Look, 2, SubEntityBindingPointCategory.HookPointCategoryMountDriver)] };
        var options = new List<HumanOption>
        {
            new(new HumanOptionCaseTitleOption(new("Captain", 17))),
            new(new HumanOptionCaseOrnamentOption(new(253, 0, null, 29))),
        };
        var actor = MapActorMessages.Player(character, 312, 7, 9_000_000_003, mount, alignment, null, options);
        options.Clear();
        Assert.Equal(character.Id, actor.ActorId);
        Assert.Equal(new EntityDisposition(312, Direction.NorthEast, null, null), actor.Disposition);
        var human = Human(actor);
        Assert.Equal(9_000_000_003, human.AccountId);
        Assert.Equal(alignment, human.AlignmentInformation);
        Assert.Null(human.MutantInformation);
        Assert.Equal(Hierarchy.RolePlayer, human.Role);
        Assert.Equal(new[] { Restriction.CannotAttackAsMutant }, human.Restrictions);
        Assert.Equal(2, human.Options.Count);
        Assert.Equal(253, Assert.IsType<HumanOptionCaseOrnamentOption>(human.Options[1].OptionCase).Value.PlayerLevel);
        Assert.Equal(73, Assert.Single(actor.ActorInformation!.Look.SubEntities).SubEntityLook.Bones);

        var mutant = MapActorMessages.Player(character, 0, 0, 9_000_000_003, Look, null, new(6, 801), []);
        Assert.Null(Human(mutant).AlignmentInformation);
        Assert.Equal(new MutantInformation(6, 801), Human(mutant).MutantInformation);
        Assert.Empty(Human(mutant).Options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Monster_groups_detach_members_and_alternatives_and_keep_the_leaders_look_absent(bool modular)
    {
        var group = new MobSpawnManager.MobGroup
        {
            MobId = -9_000_000_001, CellId = 201, Orientation = 5, Modular = modular,
            Members = Enumerable.Range(0, 5).Select(i => new MobSpawnManager.MobMember
            {
                Monster = new() { Id = 81 + i, Look = $"{{{91 + i}|45|1=123456|115}}", Grades = [new() { Level = 17 + i }] },
                GradeIndex = 0, Level = i == 0 ? 99 : 0,
            }).ToList(),
        };
        var actor = MapActorMessages.MonsterGroup(group, false);
        group.Members[0].Monster.Id = 999;
        group.Members[1].Monster.Look = "{999}";
        group.Members.Clear();
        Assert.Equal(91, actor.ActorInformation!.Look.Bones);
        var monsters = Assert.IsType<RolePlayActorCaseMonsterGroupActor>(Roleplay(actor).ActorCase).Value;
        Assert.Equal(Alignment.Without, monsters.Alignment);
        Assert.Equal(-1, monsters.LootShare);
        Assert.Equal(81, monsters.Identification.MainCreature.Gid);
        Assert.Equal(99, monsters.Identification.MainCreature.Level);
        Assert.Null(monsters.Identification.MainCreature.Look);
        Assert.Equal(new[] { 82, 83, 84, 85 }, monsters.Identification.Underlings.Select(m => m.Gid));
        Assert.Equal(92, monsters.Identification.Underlings[0].Look!.Bones);
        Assert.Equal(18, monsters.Identification.Underlings[0].Level);
        Assert.All(monsters.Identification.Underlings, m => Assert.Equal(1, m.Grade));
        Assert.Equal(modular ? new[] { 1, 5 } : [], monsters.Identification.Alternatives.Select(a => a.PlayerCount));
        Assert.All(monsters.Identification.Alternatives.SelectMany(a => a.Monsters), m => Assert.Null(m.Look));
    }

    [Fact]
    public void House_short_plaques_preserve_distinct_admin_and_door_lock_policies_optional_prices_and_account_presence()
    {
        var owned = MapHouseMessages.Instance(22, "Owner", "TAG", 9_000_000_005, false, null);
        Assert.True(owned.HasOwner);
        Assert.False(owned.IsAdminLocked);
        Assert.True(owned.IsLocked);
        Assert.False(owned.IsSaleLocked);
        Assert.Equal(9_000_000_005, owned.Price);
        Assert.Equal(new AccountTag("Owner", "TAG"), owned.AccountTag);
        Assert.Equal(22, owned.InstanceId);
        var unowned = MapHouseMessages.Instance(23, "", "", 0, true, null);
        Assert.False(unowned.HasOwner);
        Assert.True(unowned.IsAdminLocked);
        Assert.False(unowned.IsLocked);
        Assert.Null(unowned.Price);
        Assert.Equal(new AccountTag("", ""), unowned.AccountTag);
        Assert.Empty(unowned.SkillsId);
    }

    [Fact]
    public void Interactive_skills_keep_enabled_disabled_and_optional_zero_distinct_and_detach_the_list()
    {
        var skills = new List<InteractiveElementSkill> { new(114, 987, null), new(115, 988, 0) };
        var enabled = MapInteractiveMessages.Declaration(54321, -1, skills, true);
        var disabled = MapInteractiveMessages.Declaration(54322, 31, skills, false);
        skills.Clear();
        Assert.Equal(2, enabled.EnabledSkills.Count);
        Assert.Empty(enabled.DisabledSkills);
        Assert.Empty(disabled.EnabledSkills);
        Assert.Equal(2, disabled.DisabledSkills.Count);
        Assert.Null(enabled.EnabledSkills[0].NameId);
        Assert.Equal(0, disabled.DisabledSkills[1].NameId);
        Assert.True(enabled.OnCurrentMap);
        Assert.Null(enabled.AgeBonus);
        Assert.Equal(0, MapInteractiveMessages.Declaration(54323, 32, [], true, ageBonus: 0).AgeBonus);
    }

    [Fact]
    public void Placement_fights_keep_teams_leaders_alive_members_summon_exclusion_and_party_options()
    {
        var fight = new FightInstance(7_100_971, 91_000_971) { DefenderLeaderId = -9_000_000_002 };
        fight.Azul.Add(new() { Id = 9_000_000_001, CurrentHP = 100, Name = "Captain", Level = 253 });
        fight.Azul.Add(new() { Id = 9_000_000_003, CurrentHP = 0, Name = "Dead", Level = 77 });
        fight.Azul.Add(new() { Id = -9_000_000_004, CurrentHP = 100, Invocador = 9_000_000_001 });
        fight.Rojo.Add(new() { Id = -9_000_000_005, IsMonster = true, MonsterId = 31, GradeIndex = 5, CurrentHP = 100 });
        fight.SetOption(FightInstance.Azules, FightInstance.OptionPartyOnly, true);
        var snapshot = FightHandler.MapSnapshotOf(fight);
        Assert.Equal(7_100_971, snapshot.FightId);
        Assert.Equal(FightType.PvM, snapshot.Type);
        Assert.Equal(new[] { 0, 0 }, snapshot.TeamsPosition);
        var blue = snapshot.TeamsInformation[0];
        var red = snapshot.TeamsInformation[1];
        Assert.Equal(9_000_000_001, blue.LeaderId);
        Assert.Equal(TeamType.Player, blue.TeamType);
        Assert.Equal(Team.Challenger, blue.Team);
        Assert.Equal(Alignment.Without, blue.Side);
        Assert.Equal(new FightTeamMemberCharacterInformation(253, "Captain"),
            Assert.IsType<FightTeamMemberInformationCaseCharacter>(Assert.Single(
                Assert.IsType<FightTeamInformationCaseMembers>(blue.TeamInformationCase).Value.Members).MemberCase).Value);
        Assert.Equal(-9_000_000_002, red.LeaderId);
        Assert.Equal(TeamType.Monster, red.TeamType);
        Assert.Equal(6, Assert.IsType<FightTeamMemberInformationCaseMonsterMember>(Assert.Single(
            Assert.IsType<FightTeamInformationCaseMembers>(red.TeamInformationCase).Value.Members).MemberCase).Value.Grade);
        Assert.True(snapshot.TeamsOptions[0].IsRestrictedToParty);
        Assert.False(snapshot.TeamsOptions[1].IsRestrictedToParty);
        fight.Azul.Clear();
        Assert.Single(Assert.IsType<FightTeamInformationCaseMembers>(blue.TeamInformationCase).Value.Members);
    }

    [Fact]
    public async Task Direct_map_delivery_uses_owner_codec_detached_collections_and_selected_empty_specific_state()
    {
        var source = new Source();
        var frames = new List<GameFrame>();
        var codec = new RecordingCodec(frames);
        int sends = 0;
        var session = new GameSession(codec, bytes => { Assert.Equal(new byte[] { 0x71 }, bytes); sends++; return Task.CompletedTask; });
        session.State.MapId = 9_000_000_001;
        session.State.CharacterId = 9_000_000_002;
        source.Owner = session;
        var character = new DbCharacter { Id = session.CharacterId };
        var ambient = GameSession.SinSocket();
        var messages = new MapInformationMessages(source);
        using (SessionContext.Push(ambient))
        using (await session.EnterTurnAsync())
        {
            await messages.SendAsync(session, character).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, session.UnoCadaVez.CurrentCount);
            Assert.Same(ambient, SessionContext.Actual);
        }
        source.ActorsList.Clear();
        source.States[0] = new(999, 999, 0, false);
        source.Elements[0] = new(999, 0, [], [], false, null);
        var frame = Assert.Single(frames);
        var map = Assert.IsType<MapComplementaryInformationEvent>(frame.Content);
        Assert.Equal(session.MapId, map.MapId);
        Assert.Equal(313, map.SubareaId);
        Assert.Equal(-99, Assert.Single(map.Actors).ActorId);
        Assert.Equal(873, Assert.Single(map.InteractiveElements).ElementId);
        Assert.Equal(new StatedElement(873, 283, 2, true), Assert.Single(map.StatedElements));
        Assert.Equal(new MapObstacle(105, MapObstacleState.ObstacleOpened), Assert.Single(map.Obstacles));
        Assert.IsType<MapComplementaryGuildInformationCaseGuildHallSelectionInformation>(
            Assert.IsType<MapSpecificComplementaryInformationCaseGuildInformation>(map.SpecificCase).Value.ComplementaryInformationCase);
        Assert.Null(frame.Uid);
        Assert.Equal(1, sends);
        Assert.Equal(0, codec.Decodes);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("codec")]
    [InlineData("transport")]
    public async Task Direct_snapshot_failures_propagate_without_raw_fallback_or_retry(string stage)
    {
        var failure = new IOException("snapshot failed");
        var frames = new List<GameFrame>();
        var codec = new RecordingCodec(frames) { Failure = stage == "codec" ? failure : null };
        int sends = 0;
        var session = new GameSession(codec, _ => { sends++; return Task.FromException(failure); });
        session.State.MapId = 9_000_000_001;
        var source = new Source { Owner = session, Failure = stage == "source" ? failure : null };
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new MapInformationMessages(source).SendAsync(session, new())));
        Assert.Equal(stage == "source" ? 0 : 1, frames.Count);
        Assert.Equal(stage == "transport" ? 1 : 0, sends);
        Assert.Equal(0, codec.Decodes);
    }

    private static RolePlayActor Roleplay(ActorPositionInformation actor)
        => Assert.IsType<ActorInformationCaseRolePlayActor>(actor.ActorInformation!.ActorCase).Value;
    private static HumanoidInformation Human(ActorPositionInformation actor)
        => Assert.IsType<NamedActorCaseHumanoidInformation>(Assert.IsType<RolePlayActorCaseNamedActor>(Roleplay(actor).ActorCase).Value.ActorCase).Value;

    private sealed class Source : IMapInformationSource
    {
        internal GameSession Owner = null!;
        internal Exception? Failure;
        internal List<ActorPositionInformation> ActorsList = [MapActorMessages.Roleplay(-99, 25, 2, Look, new RolePlayActorCasePortalActor(new(5, 37)))];
        internal InteractiveElement[] Elements = [new(873, -1, [new(114, 777, null)], [], true, null)];
        internal StatedElement[] States = [new(873, 283, 2, true)];
        private void Check(GameSession session) { Assert.Same(Owner, session); Assert.Same(Owner, SessionContext.Actual); }
        public IReadOnlyList<ActorPositionInformation> Actors(GameSession session, DbCharacter character)
        { Check(session); if (Failure != null) throw Failure; return ActorsList; }
        public int SubAreaOf(long mapId) { Assert.Equal(Owner.MapId, mapId); return 313; }
        public IReadOnlyList<MapObstacle> Obstacles(long mapId) => [new(105, MapObstacleState.ObstacleOpened)];
        public IReadOnlyList<House> Houses(long mapId) => [];
        public (InteractiveElement[], StatedElement[]) Interactives(GameSession session) { Check(session); return (Elements, States); }
        public IReadOnlyList<FightCommonInformation> Fights(long mapId) => [];
        public MapSpecificComplementaryInformationCase Specific(GameSession session, DbCharacter character)
        { Check(session); return new MapSpecificComplementaryInformationCaseGuildInformation(new(new MapComplementaryGuildInformationCaseGuildHallSelectionInformation(new()))); }
    }

    private sealed class RecordingCodec(List<GameFrame> frames) : IGameCodec
    {
        internal int Decodes;
        internal Exception? Failure;
        public byte[] Encode(GameFrame frame) { frames.Add(frame); if (Failure != null) throw Failure; return [0x71]; }
        public GameFrame Decode(ReadOnlySpan<byte> bytes) { Decodes++; throw new InvalidOperationException("Unexpected map snapshot decode"); }
    }
}
