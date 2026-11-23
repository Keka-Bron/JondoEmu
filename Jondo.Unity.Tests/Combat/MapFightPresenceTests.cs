using System.Collections.Concurrent;
using System.Reflection;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat;

[Collection("npc tables")]
public sealed partial class MapFightPresenceTests
{
    private const long Map = 91_968_001, Arena = 91_968_002, Id = 7_968_001, Person = 9_968_000_001;
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static Task<GameEntryResult> DispatchReady(GameSession session, bool ready)
        => DispatchPlacement(session, new FightReadyRequest(ready));

    private static async Task<GameEntryResult> DispatchPlacement(GameSession session, GameRequest request)
    {
        using var turn = await session.EnterTurnAsync();
        return await GameNodeProxy.DispatchTypedAsync(session, new(request, -1),
            new GameEntryService(session, new GameEntryBackend()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fight_map_opening_is_private_and_joiners_skip_the_roleplay_prefix(bool joining)
    {
        using var fixture = new FightFixture();
        var fight = fixture.Fight;
        using var challenger = new Recipient(Person, Arena, Id);
        using var challenged = new Recipient(Person + 1, Arena, Id);
        fight.DefenderLeaderId = challenged.Session.CharacterId;
        fight.Rojo.Add(new() { Id = challenged.Session.CharacterId, CurrentHP = 23, MaxHP = 42 });
        using var ambient = SessionContext.Push(challenger.Session);
        await FightHandler.SendFightEntryAsync(challenged.Session, fight, joining);
        Assert.Empty(challenger.Sequence);

        var sequence = challenged.Sequence.AsEnumerable();
        if (!joining)
        {
            Assert.Equal(new MapChangeOrientationEvent(challenged.Session.CharacterId, Direction.East), challenged.Sequence[0]);
            Assert.Equal(new ContextRemoveElementEvent(fight.DefenderLeaderId), challenged.Sequence[1]);
            sequence = sequence.Skip(2);
        }
        Assert.Collection(sequence,
            e => Assert.IsType<ContextDestroyEvent>(e),
            e => Assert.Equal(new ContextCreationEvent(GameContext.Fight), e),
            e => Assert.Equal(new MapCurrentEvent(Arena), e),
            e => Assert.IsType<TimeEvent>(e),
            e => Assert.Equal("kuq", e), // Existing life update between clock and environment.
            e => Assert.IsType<ZaapKnownListEvent>(e));
        Assert.All(challenged.Delivered, frame => Assert.Null(frame.Uid));
    }

    [Fact]
    public async Task Attack_with_an_unknown_group_is_handled_without_entering_a_fight()
    {
        using var player = new Recipient(Person, Map);
        Assert.Equal(GameEntryResult.Handled, await DispatchPlacement(player.Session, new AttackMonsterRequest(-123456)));
        Assert.False(player.Session.State.IsInFight);
        Assert.Empty(player.Sequence);
    }
    [Fact]
    public async Task Placement_position_offers_privately_then_broadcasts_actual_cells_to_the_board()
    {
        using var fixture = new FightFixture();
        var fight = fixture.Fight;
        fight.BluePlacementCells.AddRange([284, 285, 271]);
        fight.Buscar(Person).CellId = 284;
        fight.Azul.Add(new() { Id = Person + 1, CellId = 285, CurrentHP = 100 });
        fight.Rojo[0].CellId = 270;
        fight.Rojo[1].CellId = 269;
        using var player = new Recipient(Person, Arena, Id);
        using var ally = new Recipient(Person + 1, Arena, Id);
        using var spectator = new Recipient(Person + 2, Arena, Id);
        spectator.Session.State.SpectatingFightId = Id;
        var spectators = (ConcurrentDictionary<long, ConcurrentDictionary<long, byte>>)typeof(FightHandler)
            .GetField("_spectators", PrivateStatic)!.GetValue(null)!;
        spectators[Id] = new([new(Person + 2, 0)]);
        using var bystander = new Recipient(Person + 3, Map);
        player.Session.State.IsInFight = true;

        Assert.Equal(GameEntryResult.Handled, await DispatchPlacement(player.Session, new FightPlacementPositionRequest(Person + 1, 285)));
        var offer = Assert.IsType<FightPlacementSwapPositionsOfferEvent>(Assert.Single(player.Sequence));
        Assert.True(offer.RequestId > 0);
        Assert.Equal(new FightPlacementSwapPositionsOfferEvent(Person + 1, offer.RequestId, Person), Assert.Single(ally.Sequence));
        Assert.Empty(spectator.Sequence);
        Assert.Equal(284, fight.Buscar(Person).CellId);

        Assert.Equal(GameEntryResult.Handled, await DispatchPlacement(player.Session, new FightPlacementPositionRequest(Person, 271)));
        Assert.Equal(271, fight.Buscar(Person).CellId);
        foreach (var recipient in new[] { player, ally, spectator })
        {
            var positions = Assert.IsType<EntitiesDispositionEvent>(recipient.Sequence.Last()).Added;
            Assert.Equal(new EntityDisposition(271, Direction.NorthWest, Person, null), positions[0]);
            Assert.Equal(new long?[] { Person, Person + 1, -1, -2 }, positions.Select(p => p.EntityId));
            Assert.Equal(new int?[] { 271, 285, 270, 269 }, positions.Select(p => p.CellId));
            Assert.All(recipient.Delivered, f => Assert.Null(f.Uid));
        }
        Assert.Empty(bystander.Sequence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Queued_placement_position_is_discarded_if_the_player_leaves_or_combat_starts(bool leaves)
    {
        using var fixture = new FightFixture();
        var fight = fixture.Fight;
        var actor = fight.Buscar(Person);
        actor.CellId = 300;
        actor.MaxMP = actor.CurrentMP = 3;
        fight.BluePlacementCells.Add(314);
        using var player = new Recipient(Person, Arena, Id);
        player.Session.State.IsInFight = true;
        await fight.Turno.WaitAsync();
        Task<GameEntryResult> queued;
        try
        {
            queued = DispatchPlacement(player.Session, new FightPlacementPositionRequest(Person, 314));
            Assert.False(queued.IsCompleted);
            if (leaves) Assert.True(fight.Azul.Remove(actor));
            else fight.StartFight();
        }
        finally { fight.Turno.Release(); }
        Assert.Equal(GameEntryResult.Handled, await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(300, actor.CellId);
        Assert.Equal(3, actor.CurrentMP);
        Assert.Empty(player.Sequence);
    }

    [Theory]
    [InlineData("left")]
    [InlineData("gone")]
    [InlineData("replaced")]
    public async Task Queued_ready_cannot_follow_a_character_to_another_fight(string change)
    {
        using var first = new FightFixture();
        using var player = new Recipient(Person, Arena, Id);
        player.Session.State.IsInFight = true;
        var oldFighter = first.Fight.Buscar(Person);
        // Hold A's turn until the request has selected A and is waiting to enter it.
        Task<GameEntryResult> queued;
        FightFixture next;
        await first.Fight.Turno.WaitAsync();
        try
        {
            queued = DispatchReady(player.Session, true);
            Assert.False(queued.IsCompleted);
            if (change == "left") Assert.True(first.Fight.Azul.Remove(oldFighter));
            else if (change == "gone") first.Fight.Gone.Add(Person);
            else first.Unregister(); // Same id, different instance; old roster still intact.
            next = new FightFixture(id: change == "replaced" ? Id : Id + 1);
            player.Session.State.FightId = next.Fight.FightId;
        }
        finally { first.Fight.Turno.Release(); }

        using (next)
        {
            Assert.Equal(GameEntryResult.Handled, await queued.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(oldFighter.IsReady);
            Assert.False(next.Fight.Buscar(Person).IsReady);
            Assert.Equal(FightState.Placement, first.Fight.State);
            Assert.Equal(FightState.Placement, next.Fight.State);
            Assert.Empty(player.Sequence);
        }
    }

    [Theory]
    [InlineData("click")]
    [InlineData("party")]
    [InlineData("deadline")]
    public async Task Placement_ready_reaches_both_players_and_spectator_before_a_single_start(string trigger)
    {
        using var fixture = new FightFixture();
        var fight = fixture.Fight;
        fight.Azul.Add(new() { Id = Person + 1, CurrentHP = 100, Name = "Follower" });
        var leaderCodec = new RecordingCodec();
        var followerCodec = new RecordingCodec();
        await using var leader = await ClientPipe.OpenAsync(Person + 100, Person, Arena, codec: leaderCodec);
        await using var follower = await ClientPipe.OpenAsync(Person + 101, Person + 1, Arena, codec: followerCodec);
        foreach (var session in new[] { leader.Session, follower.Session })
        {
            session.State.IsInFight = true;
            session.State.FightId = Id;
        }
        using var watcher = new Recipient(Person + 2, Arena, Id);
        watcher.Session.State.SpectatingFightId = Id;
        var spectators = (ConcurrentDictionary<long, ConcurrentDictionary<long, byte>>)typeof(FightHandler)
            .GetField("_spectators", PrivateStatic)!.GetValue(null)!;
        spectators[Id] = new([new(Person + 2, 0)]);
        using var bystander = new Recipient(Person + 3, Map);
        var codecs = new[] { leaderCodec, followerCodec, watcher.Codec };
        var phases = new List<FightState>();
        foreach (var codec in codecs)
            codec.OnEncode = frame => { if (frame.Content is FightHumanReadyStateEvent) phases.Add(fight.State); };

        var party = Parties.Create(Person);
        try
        {
            Parties.Invite(party, Person + 1, Person);
            Parties.Accept(party, Person + 1);
            FightHandler.SetAutoOptions(Person + 1, join: false, ready: trigger == "party");
            Assert.Equal(GameEntryResult.Handled, await DispatchReady(leader.Session, false));
            Assert.False(fight.Buscar(Person).IsReady);
            Assert.All(codecs, c => Assert.Empty(c.Frames));

            Assert.Equal(GameEntryResult.Handled, await DispatchReady(leader.Session, true));
            if (trigger != "party")
            {
                Assert.Equal(FightState.Placement, fight.State);
                Assert.All(codecs, c => Assert.Equal(new FightHumanReadyStateEvent(Person, true), Assert.Single(c.Frames).Content));
                if (trigger == "click") Assert.Equal(GameEntryResult.Handled, await DispatchReady(follower.Session, true));
                else
                {
                    using var turn = await leader.Session.EnterTurnAsync();
                    using var context = SessionContext.Push(leader.Session);
                    await FightHandler.InTurnAsync(fight, () => Invoke("StartWhoeverIsLeftAsync", fight));
                }
            }
            Assert.Equal(FightState.Ongoing, fight.State);
            Assert.Equal(GameEntryResult.Handled, await DispatchReady(leader.Session, true));
            Assert.All(codecs, c => Assert.Equal(new GameFrame[] {
                new(new FightHumanReadyStateEvent(Person, true)), new(new FightHumanReadyStateEvent(Person + 1, true)) },
                c.Frames.Where(f => f.Content is FightHumanReadyStateEvent)));
            Assert.Equal(Enumerable.Repeat(FightState.Placement, 6), phases);
            Assert.Equal(new RemoveChallengeEvent((int)Id), Assert.Single(bystander.Codec.Frames).Content);
        }
        finally
        {
            FightHandler.SetAutoOptions(Person + 1, join: false, ready: false);
            Parties.Dissolve(party);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opening_sends_own_teams_then_roleplay_removals_flags_count_and_incremental_monsters(bool collector)
    {
        using var fixture = new FightFixture(collector);
        var fight = fixture.Fight;
        using var own = new Recipient(Person, Arena, Id);
        using var peer = new Recipient(Person + 1, Map);
        using var otherFight = new Recipient(Person + 2, Map, Id + 1);
        using var elsewhere = new Recipient(Person + 3, Map + 9);
        using var party = new PartyFixture(Person);
        using (SessionContext.Push(own.Session))
        using (await own.Session.EnterTurnAsync())
        using (await peer.Session.EnterTurnAsync())
        {
            await FightHandler.InTurnAsync(fight,
                () => Invoke("AfterFightOpenedAsync", fight, new MobSpawnManager.MobGroup { CellId = 313 }, 312));
            Assert.Equal(0, own.Session.UnoCadaVez.CurrentCount);
            Assert.Equal(0, peer.Session.UnoCadaVez.CurrentCount);
        }
        Assert.Collection(own.Codec.Frames,
            frame => Assert.Equal(Person, Assert.IsType<FightTeamUpdateEvent>(frame.Content).Team.LeaderId),
            frame => Assert.Equal(collector ? 2 : 0, Members(Assert.IsType<FightTeamUpdateEvent>(frame.Content).Team).Count));
        int removedActors = collector ? 3 : 2;
        Assert.Equal(collector ? new long[] { -20002, Person, Person + 9 } : new long[] { -20002, Person },
            peer.Codec.Frames.Take(removedActors).Select(frame => Assert.IsType<ContextRemoveElementEvent>(frame.Content).ElementId));
        Assert.Collection(peer.Codec.Frames.Skip(removedActors).Take(3),
            frame => Assert.Equal(new FightOptionUpdateEvent(Team.Challenger, FightOption.Unknown1, true, (int)Id), frame.Content),
            frame =>
            {
                var shown = Assert.IsType<ShowChallengeEvent>(frame.Content).FightInformation;
                Assert.Equal(new[] { 312, 313 }, shown.TeamsPosition);
                Assert.True(shown.TeamsOptions[0].IsRestrictedToParty);
                Assert.False(shown.TeamsOptions[1].IsRestrictedToParty);
                Assert.Empty(Members(shown.TeamsInformation[1]));
            },
            frame => Assert.Equal(new MapFightCountEvent(1), frame.Content));
        var teams = peer.Codec.Frames.Skip(removedActors + 3).Select(frame => Assert.IsType<FightTeamUpdateEvent>(frame.Content)).ToArray();
        Assert.Equal(new[] { Team.Challenger, Team.Defender, Team.Defender }, teams.Take(3).Select(update => update.Team.Team));
        Assert.Single(Members(teams[0].Team));
        Assert.Empty(Members(teams[1].Team));
        Assert.Equal(collector ? new[] { 2 } : new[] { 1, 2 }, teams.Skip(2).Select(update => Members(update.Team).Count));
        var first = Members(teams[2].Team)[0];
        if (collector) Assert.Equal(new FightTeamMemberTaxCollector(35, 109),
            Assert.IsType<FightTeamMemberInformationCaseTaxCollectorMember>(first.MemberCase).Value);
        else Assert.Equal(new FightTeamMemberMonster(492, 6),
            Assert.IsType<FightTeamMemberInformationCaseMonsterMember>(first.MemberCase).Value);
        Assert.Empty(otherFight.Codec.Frames);
        Assert.Empty(elsewhere.Codec.Frames);
        Assert.All(peer.Codec.Frames, frame => Assert.Null(frame.Uid));
        // Snapshots do not retain mutable fighters/lists.
        fight.Rojo[0].MonsterId = 999;
        fight.Rojo.Clear();
        Assert.Equal(collector ? 2 : 1, Members(teams[2].Team).Count);
        Assert.Empty(own.Raw);
        Assert.Empty(peer.Raw);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Map_failures_are_isolated_per_recipient_and_do_not_stop_later_updates(bool transport)
    {
        using var fixture = new FightFixture();
        using var peer = new Recipient(Person + 1, Map);
        using var broken = new Recipient(Person + 2, Map, failTransport: transport);
        broken.Codec.Fail = !transport;
        using (await peer.Session.EnterTurnAsync())
            await Invoke("ShowFightOnMapAsync", fixture.Fight).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(8, peer.Codec.Frames.Count);
        Assert.Equal(peer.Codec.Frames.Count, broken.Codec.Frames.Count);
        Assert.Empty(broken.Delivered);
        Assert.True(SessionRegistry.TryGet(broken.Session.Id, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Own_opening_failure_propagates_before_map_delivery(bool transport)
    {
        using var fixture = new FightFixture();
        using var own = new Recipient(Person, Arena, Id, failTransport: transport);
        own.Codec.Fail = !transport;
        using var peer = new Recipient(Person + 1, Map);
        await Assert.ThrowsAsync<IOException>(() => Invoke("AfterFightOpenedAsync", fixture.Fight, null, 312));
        Assert.Single(own.Codec.Frames);
        Assert.Empty(peer.Codec.Frames);
    }

    [Fact]
    public async Task Dungeon_regrowth_sends_removals_before_detached_growing_teams_only_to_roleplay()
    {
        using var fixture = new FightFixture();
        using var peer = new Recipient(Person + 1, Map);
        using var fighter = new Recipient(Person + 2, Map, Id);
        var removed = new List<Fighter> { new() { Id = -9 }, new() { Id = -10 } };
        var added = fixture.Fight.Rojo.ToList();
        await Invoke("RegrowthOnTheMapAsync", fixture.Fight, (removed, added));
        Assert.Equal(new FightTeamRemoveMemberEvent((int)Id, Team.Defender, -9), peer.Codec.Frames[0].Content);
        Assert.Equal(new FightTeamRemoveMemberEvent((int)Id, Team.Defender, -10), peer.Codec.Frames[1].Content);
        var updates = peer.Codec.Frames.Skip(2).Select(frame => Assert.IsType<FightTeamUpdateEvent>(frame.Content)).ToArray();
        Assert.Equal(new[] { 1, 2 }, updates.Select(update => Members(update.Team).Count));
        added.Clear();
        Assert.Equal(new long[] { -1, -2 }, Members(updates[1].Team).Select(member => member.CharacterId));
        Assert.Empty(fighter.Codec.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Option_updates_keep_fight_and_spectator_audiences_and_only_notify_the_map_during_placement(bool ongoing)
    {
        using var fixture = new FightFixture();
        var fight = fixture.Fight;
        if (ongoing) fight.StartFight();
        using var own = new Recipient(Person, Arena, Id);
        using var peer = new Recipient(Person + 1, Map);
        using var watcher = new Recipient(Person + 2, Arena, Id);
        watcher.Session.State.SpectatingFightId = Id;
        var spectators = (ConcurrentDictionary<long, ConcurrentDictionary<long, byte>>)typeof(FightHandler)
            .GetField("_spectators", PrivateStatic)!.GetValue(null)!;
        spectators[Id] = new([new(Person + 2, 0)]);
        try
        {
            using (SessionContext.Push(own.Session))
            {
                await Invoke("SwitchOptionAsync", fight, Person, FightInstance.OptionHelp);
                await Invoke("SwitchOptionAsync", fight, Person, FightInstance.OptionHelp);
            }
            Assert.Equal(new[] { true, false }, own.Codec.Frames.Select(frame => Assert.IsType<FightOptionUpdateEvent>(frame.Content).State));
            Assert.Equal(own.Codec.Frames, watcher.Codec.Frames);
            Assert.Equal(ongoing ? 0 : 2, peer.Codec.Frames.Count);
        }
        finally { spectators.TryRemove(Id, out _); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Placement_departure_keeps_board_removals_separate_from_roleplay_flags_and_teams(bool lastPlayer)
    {
        using var fixture = new FightFixture();
        var fight = fixture.Fight;
        if (!lastPlayer) fight.Azul.Add(new() { Id = Person + 5, Name = "Remaining", CurrentHP = 100 });
        await using var own = await ClientPipe.OpenAsync(Person + 1_000_000_000, Person, Arena);
        own.Session.State.FightId = Id;
        own.Session.State.IsInFight = true;
        own.Session.State.RoleplayMapId = Map;
        own.Session.State.RoleplayCellId = 312;
        using var remaining = new Recipient(Person + 5, Arena, Id);
        using var peer = new Recipient(Person + 1, Map);
        using (SessionContext.Push(own.Session))
        using (await own.Session.EnterTurnAsync())
        using (await remaining.Session.EnterTurnAsync())
            await FightHandler.InTurnAsync(fight, () => FightHandler.LeavePlacementAsync(own.ToClient, fight))
                .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "jxa", "kml", "kmp", "ktz", "jru", "lqu" }, (await own.Take(6)).Select(packet => packet.Opcode));
        Assert.Equal(0, own.Session.State.FightId);
        Assert.Equal(Map, own.Session.MapId);
        if (lastPlayer)
        {
            Assert.Equal(new GamePayload[] { new RemoveChallengeEvent((int)Id), new MapFightCountEvent(0) },
                peer.Codec.Frames.Select(frame => frame.Content));
            Assert.Empty(remaining.Sequence);
        }
        else
        {
            Assert.Collection(remaining.Sequence,
                entry => Assert.Equal("kar", entry),
                entry => Assert.Equal(new ContextRemoveElementEvent(Person), entry),
                entry => Assert.Equal(new FightTeamRemoveMemberEvent((int)Id, Team.Challenger, Person), entry),
                entry => Assert.Equal("jzu", entry),
                entry => Assert.Equal(Person + 5, Assert.IsType<FightTeamUpdateEvent>(entry).Team.LeaderId));
            Assert.IsType<FightTeamUpdateEvent>(Assert.Single(peer.Codec.Frames).Content);
        }
    }

    [Fact]
    public async Task Flag_removal_does_not_change_count_and_map_entry_omits_zero_while_fight_end_explicitly_sends_zero()
    {
        using var fixture = new FightFixture();
        using var peer = new Recipient(Person + 1, Map);
        using (SessionContext.Push(peer.Session))
        {
            await FightHandler.SendFightCountAsync(peer.Session, Map);
            await Invoke("SwordsGoneAsync", fixture.Fight);
            await Invoke("FightOffTheMapAsync", fixture.Fight);
            await FightHandler.SendFightCountAsync(peer.Session, Map);
        }
        Assert.Equal(new GamePayload[] { new MapFightCountEvent(1), new RemoveChallengeEvent((int)Id), new MapFightCountEvent(0) },
            peer.Codec.Frames.Select(frame => frame.Content));
    }

    [Fact]
    public async Task Spectator_departure_sends_encoded_removal_before_context_change_and_only_to_the_old_roleplay_map()
    {
        using var fixture = new FightFixture();
        fixture.Fight.StartFight();
        await using var own = await ClientPipe.OpenAsync(8_968_000_001, Person + 7, Map);
        using var peer = new Recipient(Person + 1, Map);
        using var fighter = new Recipient(Person + 2, Map, Id + 1);
        using var elsewhere = new Recipient(Person + 3, Arena);
        try
        {
            using (SessionContext.Push(elsewhere.Session))
            using (await own.Session.EnterTurnAsync())
            using (await peer.Session.EnterTurnAsync())
                await FightHandler.WatchFromTheMapAsync(own.Session, Id).WaitAsync(TimeSpan.FromSeconds(5));
            var removal = new GameCodec().Decode(Assert.Single(peer.Raw));
            Assert.Equal(new ContextRemoveElementEvent(Person + 7), removal.Content);
            Assert.Null(removal.Uid);
            Assert.Empty(peer.Codec.Frames);
            Assert.Empty(fighter.Codec.Frames);
            Assert.Empty(fighter.Raw);
            Assert.Empty(elsewhere.Codec.Frames);
            Assert.Empty(elsewhere.Raw);
            Assert.Equal(Id, own.Session.State.SpectatingFightId);
            Assert.Equal(Map, own.Session.State.RoleplayMapId);
            Assert.Equal(Arena, own.Session.MapId);
            Assert.Equal(new[] { "kml", "kmp", "jru", "lqu" }, (await own.Take(4)).Select(packet => packet.Opcode));
        }
        finally { FightHandler.ForgetSpectator(own.Session); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Dream_power_sends_own_goblin_removals_before_group_removal_and_failure_stops_the_remaining_work(int failAt)
    {
        MapManager.Initialize();
        Interactives.Initialize();
        const long owner = Person + 20, map = Map + 20;
        var codec = new RecordingCodec { FailAt = failAt };
        await using var own = await ClientPipe.OpenAsync(owner + 1_000_000_000, owner, map, codec: codec);
        using var peer = new Recipient(Person + 21, map);
        var dreams = (ConcurrentDictionary<long, Dreams.Sueno>)typeof(Dreams).GetField("_enCurso", PrivateStatic)!.GetValue(null)!;
        var npcs = (Dictionary<long, List<Npcs.Spawn>>)typeof(Npcs).GetProperty("_byMap", PrivateStatic)!.GetValue(null)!;
        var dream = new Dreams.Sueno { CharacterId = owner, Nombre = "Presence", Nivel = 200, Dificultad = 1 };
        var room = new Dreams.Sala { Id = 0, MapaDeLaSala = map, Goblin = Dreams.Gobserico, Plantado = -20020 };
        room.Miembros.Add((492, 1));
        dream.Salas.Add(room);
        Assert.True(dreams.TryAdd(owner, dream));
        npcs.Add(map, [new() { NpcId = room.Goblin, ContextualId = -10020 }, new() { NpcId = room.Goblin, ContextualId = -10021 }]);
        try
        {
            using (SessionContext.Push(own.Session))
            using (await own.Session.EnterTurnAsync())
            {
                var sending = DreamHandler.GoblinPowerAsync(own.Session, [DreamHandler.HideDreamNpc, DreamHandler.ClearDreamRoom]);
                if (failAt > 0) await Assert.ThrowsAsync<IOException>(() => sending);
                else await sending;
            }
            Assert.Equal(new long[] { -10020, -10021, -20020 }.Take(failAt == 0 ? 3 : failAt),
                codec.Frames.Select(frame => Assert.IsType<ContextRemoveElementEvent>(frame.Content).ElementId));
            for (int i = 0; i < (failAt == 0 ? 3 : failAt - 1); i++)
                Assert.Equal(new byte[] { 0x71 }, await Jondo.Protocol.NetworkMessage.ReadFrameAsync(own.FromServer).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(room.GoblinGone);
            Assert.Equal(failAt != 1, room.Hecha);
            Assert.Equal(failAt == 0, DatabaseManager.LoadDream(owner) != null);
            Assert.Empty(peer.Codec.Frames);
            Assert.Empty(peer.Raw);
            Assert.False(own.FromServer.DataAvailable);
        }
        finally
        {
            dreams.TryRemove(owner, out _);
            npcs.Remove(map);
            DatabaseManager.DeleteDream(owner);
        }
    }

    private static IReadOnlyList<FightTeamMemberInformation> Members(FightTeamInformation team)
        => Assert.IsType<FightTeamInformationCaseMembers>(team.TeamInformationCase).Value.Members;
    private static Task Invoke(string method, params object?[] args)
        => (Task)typeof(FightHandler).GetMethod(method, PrivateStatic)!.Invoke(null, args)!;

    private sealed class FightFixture : IDisposable
    {
        private readonly ConcurrentDictionary<long, FightInstance> _active = (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
            .GetField("_activeFights", PrivateStatic)!.GetValue(null)!;
        internal FightInstance Fight { get; }
        internal FightFixture(bool collector = false, long id = Id)
        {
            Fight = new(id, Map, Arena) { DefenderLeaderId = -20002 };
            Fight.Azul.Add(new() { Id = Person, Name = "Leader", Level = 253, CurrentHP = 100 });
            Fight.Rojo.Add(new() { Id = -1, IsMonster = true, MonsterId = 492, GradeIndex = 5,
                TaxCollectorFirstName = collector ? 35 : 0, TaxCollectorLastName = collector ? 109 : 0, CurrentHP = 100 });
            Fight.Rojo.Add(new() { Id = collector ? Person + 9 : -2, Name = "Defender", Level = 199,
                IsMonster = !collector, MonsterId = 148, GradeIndex = 2, CurrentHP = 75 });
            if (collector) Fight.TaxCollectorUuid = "collector-id";
            Assert.True(_active.TryAdd(id, Fight));
        }
        internal void Unregister() => _active.TryRemove(new KeyValuePair<long, FightInstance>(Fight.FightId, Fight));
        public void Dispose()
        {
            Fight.CancelPlacementTimer();
            Fight.CancelTurnTimer();
            Unregister();
            var ledger = typeof(FightHandler).GetField("_onTheMap", PrivateStatic)!.GetValue(null)!;
            ledger.GetType().GetMethod("TryRemove", [typeof(long), ledger.GetType().GetGenericArguments()[1].MakeByRefType()])!
                .Invoke(ledger, [Fight.FightId, null]);
            var spectators = (ConcurrentDictionary<long, ConcurrentDictionary<long, byte>>)typeof(FightHandler)
                .GetField("_spectators", PrivateStatic)!.GetValue(null)!;
            spectators.TryRemove(Fight.FightId, out _);
        }
    }

    private sealed class PartyFixture(long leader) : IDisposable
    {
        private readonly Parties.Party _party = Parties.Create(leader);
        public void Dispose() => Parties.Leave(_party, leader);
    }

    private sealed class Recipient : IDisposable
    {
        internal RecordingCodec Codec { get; } = new();
        internal GameSession Session { get; }
        internal List<GameFrame> Delivered { get; } = [];
        internal List<byte[]> Raw { get; } = [];
        internal List<object> Sequence { get; } = [];
        internal Recipient(long character, long map, long fightId = 0, bool failTransport = false)
        {
            Session = new(Codec, packet =>
            {
                if (failTransport) throw new IOException("transport failed");
                if (packet.Length == 1 && packet[0] == 0x71)
                {
                    Delivered.Add(Codec.Frames[^1]);
                    Sequence.Add(Codec.Frames[^1].Content);
                }
                else
                {
                    Raw.Add(packet);
                    Sequence.Add(ConnectionProtocol.ReadOpcode(packet)!);
                }
                return Task.CompletedTask;
            });
            Session.BindAccount(character + 1_000_000_000, 1);
            Session.State.CharacterId = character;
            Session.State.MapId = map;
            Session.State.FightId = fightId;
            Session.EnterWorld();
            Assert.True(SessionRegistry.Register(Session));
        }
        public void Dispose() => SessionRegistry.Unregister(Session);
    }

    private sealed class RecordingCodec : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        internal Action<GameFrame>? OnEncode { get; set; }
        internal bool Fail;
        internal int FailAt;
        public byte[] Encode(GameFrame frame)
        {
            OnEncode?.Invoke(frame);
            Frames.Add(frame);
            if (Fail || FailAt == Frames.Count) throw new IOException("codec failed");
            return [0x71];
        }
        public GameFrame Decode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("Unexpected presence decode");
    }
}
