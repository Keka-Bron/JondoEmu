using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class SpellMessagesTests
{
    [Fact]
    public void Ordinary_snapshot_keeps_melee_first_then_catalogue_order_and_grades_with_special_types()
    {
        var own = new Recipient();
        own.Session.State.ChosenSpells[11] = 71_002;
        var backend = new Backend
        {
            LearnedSpells = [72_001, 72_002],
            KnownSpells = [new(11, 71_002, 4), new(12, 71_003, 2),
                new(13, 72_001, 3, special: true), new(-72_002, 72_002, 1, special: true)],
        };
        var wrong = new Recipient(91_002, 81_002);
        wrong.Session.State.ChosenSpells[11] = 71_999;
        using var context = SessionContext.Push(wrong.Session);

        var snapshot = new SpellMessages(backend).Snapshot(own.Session);

        Assert.Equal(new SpellItem[] { new(1, 0, SpellType.Unknown2), new(4, 71_002, SpellType.Unknown1),
            new(2, 71_003, SpellType.Unknown1), new(3, 72_001, SpellType.Unknown2), new(1, 72_002, SpellType.Unknown2) },
            snapshot.HumanSpells);
        Assert.Empty(snapshot.MutantSpells);
        Assert.Equal((8, 154), (backend.Breed, backend.Level));
        Assert.Equal(new[] { KeyValuePair.Create(11, 71_002) }, backend.Chosen!);
        Assert.Equal(new[] { 72_001, 72_002 }, backend.SelectedLearned!);
        Assert.Equal(91_001, backend.Account);
        Assert.Equal(81_001, backend.Character);
        Assert.Same(own.Session, backend.MutationOwner);
        Assert.Equal(1, backend.Reads.Count(stage => stage == "administrator"));
        Assert.DoesNotContain("all-specials", backend.Reads);
        Assert.DoesNotContain("grants", backend.Reads);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Fact]
    public void Administrator_policy_is_read_once_per_snapshot_and_revocation_uses_saved_learning_again()
    {
        var backend = new Backend { Administrator = true, LearnedSpells = [72_001], AllSpecials = [72_001, 72_002],
            Grants = [(73_001, 1), (73_002, 2)] };
        backend.SelectKnown = (_, _, _, learned) => learned.Select(spell => new SpellTable.KnownSpell(-spell, spell, 3, true)).ToArray();
        var service = new SpellMessages(backend);
        var own = new Recipient();

        var administrator = service.Snapshot(own.Session);
        backend.Administrator = false;
        var ordinary = service.Snapshot(own.Session);

        Assert.Equal(new SpellItem[] { new(1, 0, SpellType.Unknown2), new(3, 72_001, SpellType.Unknown2),
            new(3, 72_002, SpellType.Unknown2), new(1, 73_001, SpellType.Unknown1), new(2, 73_002, SpellType.Unknown1) },
            administrator.HumanSpells);
        Assert.Equal(new SpellItem[] { new(1, 0, SpellType.Unknown2), new(3, 72_001, SpellType.Unknown2) }, ordinary.HumanSpells);
        Assert.Equal(2, backend.Reads.Count(stage => stage == "administrator"));
        Assert.Single(backend.Reads, stage => stage == "learned");
        Assert.Single(backend.Reads, stage => stage == "all-specials");
        Assert.Single(backend.Reads, stage => stage == "grants");
    }

    [Fact]
    public void Mutation_keeps_the_human_book_and_has_its_own_grades_type_and_empty_reset_after_removal()
    {
        var backend = new Backend { KnownSpells = [new(11, 71_001, 4)], Mutation = [(74_001, 2), (74_002, 5)] };
        var service = new SpellMessages(backend);
        var own = new Recipient();
        var before = service.Snapshot(own.Session);
        backend.Mutation.Clear();
        var after = service.Snapshot(own.Session);

        Assert.Equal(new SpellItem[] { new(2, 74_001, SpellType.Unknown6), new(5, 74_002, SpellType.Unknown6) }, before.MutantSpells);
        Assert.Equal(new SpellItem[] { new(1, 0, SpellType.Unknown2), new(4, 71_001, SpellType.Unknown1) }, before.HumanSpells);
        Assert.Equal(before.HumanSpells, after.HumanSpells);
        Assert.Empty(after.MutantSpells);
    }

    [Fact]
    public void Snapshots_detach_selected_inputs_and_all_output_lists_from_later_mutation()
    {
        var own = new Recipient();
        own.Session.State.ChosenSpells[11] = 71_002;
        var backend = new Backend { Administrator = true, AllSpecials = [72_001], KnownSpells = [new(11, 71_002, 4)],
            Grants = [(73_001, 1)], Mutation = [(74_001, 2)] };
        var snapshot = new SpellMessages(backend).Snapshot(own.Session);
        own.Session.State.ChosenSpells.Clear();
        backend.AllSpecials.Clear();
        backend.KnownSpells.Clear();
        backend.Grants.Clear();
        backend.Mutation.Clear();

        Assert.Equal(new[] { KeyValuePair.Create(11, 71_002) }, backend.Chosen!);
        Assert.Equal(new[] { 72_001 }, backend.SelectedLearned!);
        Assert.Equal(new SpellItem[] { new(1, 0, SpellType.Unknown2), new(4, 71_002, SpellType.Unknown1),
            new(1, 73_001, SpellType.Unknown1) }, snapshot.HumanSpells);
        Assert.Equal(new SpellItem[] { new(2, 74_001, SpellType.Unknown6) }, snapshot.MutantSpells);
    }

    [Fact]
    public void Empty_catalogue_selection_still_has_the_real_melee_entry_and_no_mutant_spells()
    {
        var snapshot = new SpellMessages(new Backend()).Snapshot(new Recipient().Session);
        Assert.Equal(new SpellItem(1, 0, SpellType.Unknown2), Assert.Single(snapshot.HumanSpells));
        Assert.Empty(snapshot.MutantSpells);
    }

    [Fact]
    public async Task Refresh_sends_one_detached_owned_snapshot_before_the_bar_and_reuses_the_existing_turn()
    {
        var backend = new Backend { KnownSpells = [new(11, 71_001, 3)], Mutation = [(74_001, 2)] };
        var own = new Recipient(send: _ =>
        {
            backend.KnownSpells.Clear();
            backend.Mutation.Clear();
            return Task.CompletedTask;
        });
        var wrong = new Recipient(91_002, 81_002);
        using var context = SessionContext.Push(wrong.Session);
        using var turn = await own.Session.EnterTurnAsync();
        int bars = 0;

        await new SpellMessages(backend).RefreshAsync(own.Session, () =>
        {
            Assert.Same(own.Session, SessionContext.Actual);
            Assert.Equal(0, own.Session.UnoCadaVez.CurrentCount);
            var frame = Assert.Single(own.Codec.Frames);
            var snapshot = Assert.IsType<SpellsEvent>(frame.Content);
            Assert.Equal(new SpellItem(3, 71_001, SpellType.Unknown1), snapshot.HumanSpells[1]);
            Assert.Equal(new SpellItem(2, 74_001, SpellType.Unknown6), Assert.Single(snapshot.MutantSpells));
            Assert.Null(frame.Uid);
            Assert.Equal(1, own.Delivered);
            bars++;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, bars);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.Empty(wrong.Codec.Frames);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Fact]
    public async Task Unavailable_catalogue_omits_the_live_refresh_before_reading_state_or_sending_the_bar()
    {
        var backend = new Backend { Loaded = false };
        var own = new Recipient();
        await new SpellMessages(backend).RefreshAsync(own.Session, () => throw new Exception("Unexpected bar"));
        Assert.Equal(new[] { "catalogue" }, backend.Reads);
        Assert.Empty(own.Codec.Frames);
    }

    [Theory]
    [InlineData("catalogue")]
    [InlineData("administrator")]
    [InlineData("learned")]
    [InlineData("all-specials")]
    [InlineData("known")]
    [InlineData("grants")]
    [InlineData("mutation")]
    public async Task Source_failure_propagates_before_sending_a_partial_book_or_bar(string stage)
    {
        var failure = new IOException("spell source failed");
        var backend = new Backend { FailureStage = stage, Failure = failure, Administrator = stage is "all-specials" or "grants" };
        var own = new Recipient();
        var wrong = new Recipient(91_002, 81_002);
        using var context = SessionContext.Push(wrong.Session);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new SpellMessages(backend)
            .RefreshAsync(own.Session, () => throw new Exception("Unexpected bar after source failure"))));

        Assert.Empty(own.Codec.Frames);
        Assert.Equal(0, own.Delivered);
        Assert.Single(backend.Reads, read => read == stage);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Codec_and_transport_failures_stop_before_the_bar_without_retry(bool transport)
    {
        var failure = new IOException("spell delivery failed");
        int sends = 0;
        var own = new Recipient(encodeFailure: transport ? null : failure, send: _ =>
        {
            sends++;
            return Task.FromException(failure);
        });
        var wrong = new Recipient(91_002, 81_002);
        using var context = SessionContext.Push(wrong.Session);
        var backend = new Backend();

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new SpellMessages(backend)
            .RefreshAsync(own.Session, () => throw new Exception("Unexpected bar after send failure"))));

        Assert.IsType<SpellsEvent>(Assert.Single(own.Codec.Frames).Content);
        Assert.Equal(transport ? 1 : 0, sends);
        Assert.Equal(0, own.Delivered);
        Assert.Equal(0, own.Codec.Decodes);
        Assert.Single(backend.Reads, stage => stage == "known");
        Assert.Empty(wrong.Codec.Frames);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Fact]
    public async Task Bar_failure_keeps_the_delivered_book_and_restores_context_without_resending()
    {
        var failure = new IOException("bar failed");
        var own = new Recipient();
        var wrong = new Recipient(91_002, 81_002);
        using var context = SessionContext.Push(wrong.Session);
        int bars = 0;

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new SpellMessages(new Backend()).RefreshAsync(own.Session, () =>
        {
            bars++;
            return Task.FromException(failure);
        })));

        Assert.Equal(1, bars);
        Assert.Single(own.Codec.Frames);
        Assert.Equal(1, own.Delivered);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    private sealed class Backend : ISpellMessagesBackend
    {
        internal bool Loaded = true, Administrator;
        internal List<int> LearnedSpells = [], AllSpecials = [];
        internal List<SpellTable.KnownSpell> KnownSpells = [];
        internal List<(int Spell, int Grade)> Grants = [], Mutation = [];
        internal Func<int, int, IReadOnlyDictionary<int, int>, IReadOnlyCollection<int>, IReadOnlyList<SpellTable.KnownSpell>>? SelectKnown;
        internal long Account, Character;
        internal int Breed, Level;
        internal IReadOnlyDictionary<int, int>? Chosen;
        internal IReadOnlyCollection<int>? SelectedLearned;
        internal GameSession? MutationOwner;
        internal List<string> Reads { get; } = [];
        internal string? FailureStage;
        internal Exception? Failure;
        private void Read(string stage)
        {
            Reads.Add(stage);
            if (FailureStage == stage) throw Failure!;
        }
        public bool IsCatalogueLoaded { get { Read("catalogue"); return Loaded; } }
        public bool IsAdministrator(long accountId) { Read("administrator"); Account = accountId; return Administrator; }
        public IReadOnlyCollection<int> Learned(long characterId) { Read("learned"); Character = characterId; return LearnedSpells; }
        public IReadOnlyCollection<int> AllSpecialSpells() { Read("all-specials"); return AllSpecials; }
        public IReadOnlyList<SpellTable.KnownSpell> Known(int breed, int level, IReadOnlyDictionary<int, int> chosen, IReadOnlyCollection<int> learned)
        {
            Read("known");
            (Breed, Level, Chosen, SelectedLearned) = (breed, level, chosen, learned);
            return SelectKnown?.Invoke(breed, level, chosen, learned) ?? KnownSpells;
        }
        public IReadOnlyList<(int Spell, int Grade)> AdministratorSpells() { Read("grants"); return Grants; }
        public IReadOnlyList<(int Spell, int Grade)> MutationSpells(GameSession session)
        {
            Read("mutation");
            MutationOwner = session;
            return Mutation;
        }
    }

    private sealed class Recipient
    {
        internal RecordingCodec Codec { get; }
        internal GameSession Session { get; }
        internal int Delivered { get; private set; }
        internal Recipient(long account = 91_001, long character = 81_001, Exception? encodeFailure = null, Func<GameFrame, Task>? send = null)
        {
            Codec = new(encodeFailure);
            Session = new(Codec, async _ =>
            {
                if (send != null) await send(Codec.Frames[^1]);
                Delivered++;
            });
            Session.BindAccount(account, 1);
            Session.State.CharacterId = character;
            Session.State.Breed = 8;
            Session.State.CharacterLevel = 154;
        }
    }

    private sealed class RecordingCodec(Exception? failure) : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        internal int Decodes { get; private set; }
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (failure != null) throw failure;
            return []; // Opaque transport token; no wire/protobuf dependency.
        }
        public GameFrame Decode(ReadOnlySpan<byte> payload)
        {
            Decodes++;
            throw new InvalidOperationException("Unexpected decode in semantic spell messaging");
        }
    }
}
