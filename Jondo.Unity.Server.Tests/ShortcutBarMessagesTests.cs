using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ShortcutBarMessagesTests
{
    [Fact]
    public void Saved_spell_slots_keep_duplicates_order_and_selected_melee_zero_and_only_remember_non_weapon_entries()
    {
        var own = new Recipient();
        var backend = new Backend(own.Session) { Layout = [(0, 0), (3, 71_001), (8, 71_001), (12, 71_002)] };

        var bar = new ShortcutBarMessages(backend).SpellBar(own.Session);

        Assert.Equal(ShortcutBar.Spell, bar.BarType);
        Assert.Equal(new[] { 0, 3, 8, 12 }, bar.Shortcuts.Select(shortcut => shortcut.SlotId));
        Assert.Equal(new[] { 0, 71_001, 71_001, 71_002 }, bar.Shortcuts.Select(shortcut =>
            Assert.IsType<ShortcutCaseShortcutSpell>(shortcut.ShortcutCase).Value.SpellId));
        Assert.Equal(new[] { (3, 71_001), (8, 71_001), (12, 71_002) }, backend.Remembered);
        Assert.Equal(new[] { "mutation", "layout", "remember" }, backend.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Never_written_bars_seed_known_spells_and_deliberately_cleared_bars_keep_only_melee(bool seed)
    {
        var own = new Recipient();
        var backend = new Backend(own.Session)
        {
            SelectLayout = _ => FightSpellLayout.Build([new(1, 71_001, 1), new(2, 71_002, 3)],
                new Dictionary<int, int>(), seed).Bar,
        };

        var bar = new ShortcutBarMessages(backend).SpellBar(own.Session);

        Assert.Equal(seed ? new[] { 0, 71_001, 71_002 } : [0], bar.Shortcuts.Select(shortcut =>
            Assert.IsType<ShortcutCaseShortcutSpell>(shortcut.ShortcutCase).Value.SpellId));
        Assert.Equal(seed ? 2 : 0, backend.Remembered.Count);
    }

    [Fact]
    public void Layout_filters_unavailable_variants_and_invalid_slots_without_refilling_saved_holes()
    {
        var own = new Recipient();
        var backend = new Backend(own.Session)
        {
            SelectLayout = _ => FightSpellLayout.Build([new(1, 71_002, 3), new(2, 72_001, 1, special: true)],
                new Dictionary<int, int> { [2] = 71_001, [7] = 71_002, [9] = 71_002, [11] = 72_001, [99] = 71_002 }, false).Bar,
        };

        var bar = new ShortcutBarMessages(backend).SpellBar(own.Session);

        Assert.Equal(new[] { 0, 7, 9, 11 }, bar.Shortcuts.Select(shortcut => shortcut.SlotId));
        Assert.Equal(new[] { 0, 71_002, 71_002, 72_001 }, bar.Shortcuts.Select(shortcut =>
            Assert.IsType<ShortcutCaseShortcutSpell>(shortcut.ShortcutCase).Value.SpellId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Active_mutations_have_only_their_spells_from_slot_zero_even_when_the_mutation_has_no_spells(bool empty)
    {
        var own = new Recipient();
        var backend = new Backend(own.Session) { Mutation = empty ? [] : [74_002, 74_001] };

        var bar = new ShortcutBarMessages(backend).SpellBar(own.Session);

        Assert.Equal(ShortcutBar.Spell, bar.BarType);
        Assert.Equal(empty ? Array.Empty<int>() : [0, 1], bar.Shortcuts.Select(shortcut => shortcut.SlotId));
        Assert.Equal(empty ? Array.Empty<int>() : [74_002, 74_001], bar.Shortcuts.Select(shortcut =>
            Assert.IsType<ShortcutCaseShortcutSpell>(shortcut.ShortcutCase).Value.SpellId));
        Assert.Equal(new[] { "mutation" }, backend.Reads);
        Assert.Empty(backend.Remembered);
    }

    [Fact]
    public void General_bar_preserves_all_semantic_cases_and_detaches_the_stored_snapshot()
    {
        var own = new Recipient();
        Shortcut[] expected = [
            new(0, new ShortcutCaseShortcutObjectItem(new(81_001, 91_001))),
            new(2, new ShortcutCaseShortcutPreset(new("preset-71001"))),
            new(4, new ShortcutCaseShortcutSpell(new(71_001))),
            new(6, new ShortcutCaseShortcutSmiley(new(12))),
            new(8, new ShortcutCaseShortcutEmote(new(97))),
            new(10, new ShortcutCaseShortcutCosmeticObject(new(false, 82_001))),
            new(11, new ShortcutCaseShortcutCosmeticObject(new(true, 82_002))),
            new(12, new ShortcutCaseShortcutOutfit(new("outfit-71002"))),
        ];
        var backend = new Backend(own.Session) { General = expected.ToList() };

        var bar = new ShortcutBarMessages(backend).GeneralBar(own.Session);
        backend.General.Clear();

        Assert.Equal(ShortcutBar.General, bar.BarType);
        Assert.Equal(expected, bar.Shortcuts);
        Assert.Equal(new[] { "general" }, backend.Reads);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 82_001)]
    [InlineData(true, 82_001)]
    public void Cosmetic_enable_is_independent_of_the_object_identifier(bool enable, int gid)
    {
        var own = new Recipient();
        var backend = new Backend(own.Session) { General = [new(3, new ShortcutCaseShortcutCosmeticObject(new(enable, gid)))] };
        var shortcut = Assert.Single(new ShortcutBarMessages(backend).GeneralBar(own.Session).Shortcuts);
        var cosmetic = Assert.IsType<ShortcutCaseShortcutCosmeticObject>(shortcut.ShortcutCase).Value;
        Assert.Equal(enable, cosmetic.Enable);
        Assert.Equal(gid, cosmetic.ObjectGid);
    }

    [Fact]
    public async Task Both_snapshots_precede_delivery_and_use_the_explicit_owner_despite_ambient_state_and_transport_mutation()
    {
        var own = new Recipient();
        own.Session.BindAccount(91_001, 1);
        own.Session.State.CharacterId = 81_001;
        own.Session.State.Breed = 8;
        own.Session.State.CharacterLevel = 154;
        var backend = new Backend(own.Session) { Layout = [(0, 0), (4, 71_002)] };
        own.AfterSend = () => { backend.Layout.Clear(); backend.General.Clear(); };
        var wrong = new Recipient();
        wrong.Session.BindAccount(91_002, 1);
        wrong.Session.State.CharacterId = 81_002;
        using var scope = SessionContext.Push(wrong.Session);
        using var turn = await own.Session.EnterTurnAsync();
        var messages = new ShortcutBarMessages(backend);
        var map = new GameEntryMapMessages(TimeProvider.System, () => 5, _ => new([], []), session =>
        {
            // Populate on source access, after the opening sends have completed.
            backend.Layout.AddRange([(0, 0), (4, 71_002)]);
            backend.General.Add(new(6, new ShortcutCaseShortcutEmote(new(97))));
            return messages.Snapshot(session);
        });

        await map.SendAsync(own.Session, MapEntryOpeningMessagesTests.Empty, GameContext.RolePlay, 9_000_000_001);

        var bars = own.Codec.Frames.Select(frame => frame.Content).OfType<ShortcutBarContentEvent>().ToArray();
        Assert.Equal(new[] { ShortcutBar.Spell, ShortcutBar.General }, bars.Select(bar => bar.BarType));
        Assert.Equal(2, bars[0].Shortcuts.Count);
        Assert.Equal(new ShortcutEmote(97), Assert.IsType<ShortcutCaseShortcutEmote>(Assert.Single(bars[1].Shortcuts).ShortcutCase).Value);
        Assert.Equal(new[] { "mutation", "layout", "remember", "general" }, backend.Reads);
        Assert.All(own.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(0, own.Codec.Decodes);
        Assert.Empty(wrong.Codec.Frames);
        Assert.Same(wrong.Session, SessionContext.Actual);
        Assert.Equal(0, own.Session.UnoCadaVez.CurrentCount);
    }

    [Theory]
    [InlineData("mutation")]
    [InlineData("layout")]
    [InlineData("remember")]
    [InlineData("general")]
    public async Task Source_failures_send_neither_bar_and_stop_before_community(string stage)
    {
        var own = new Recipient();
        var failure = new IOException("shortcut source failed");
        var backend = new Backend(own.Session) { FailureStage = stage, Failure = failure };
        var service = new ShortcutBarMessages(backend);
        var map = new GameEntryMapMessages(TimeProvider.System, () => 5, _ => new([], []), service.Snapshot);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => map.SendAsync(own.Session,
            MapEntryOpeningMessagesTests.Empty, GameContext.RolePlay, 91_001)));

        Assert.DoesNotContain(own.Codec.Frames, frame => frame.Content is ShortcutBarContentEvent or CommunityChannelEvent);
        Assert.Equal(stage, backend.Reads[^1]);
        Assert.Equal(1, backend.Reads.Count(read => read == stage));
    }

    [Fact]
    public async Task Live_refresh_callback_sends_a_semantic_spell_bar_through_the_owning_codec()
    {
        var own = new Recipient();
        var backend = new Backend(own.Session) { Layout = [(0, 0), (9, 71_001)] };
        await new ShortcutBarMessages(backend).SendSpellAsync(own.Session);
        var bar = Assert.IsType<ShortcutBarContentEvent>(Assert.Single(own.Codec.Frames).Content);
        Assert.Equal(ShortcutBar.Spell, bar.BarType);
        Assert.Equal(new[] { 0, 9 }, bar.Shortcuts.Select(shortcut => shortcut.SlotId));
        Assert.Equal(0, own.Codec.Decodes);
    }

    private sealed class Backend(GameSession owner) : IShortcutBarBackend
    {
        internal List<int>? Mutation;
        internal List<(int Slot, int Spell)> Layout = [(0, 0)];
        internal List<Shortcut> General = [];
        internal IReadOnlyList<(int Slot, int Spell)> Remembered = [];
        internal Func<GameSession, IReadOnlyList<(int Slot, int Spell)>>? SelectLayout;
        internal List<string> Reads = [];
        internal string? FailureStage;
        internal Exception? Failure;

        private void Read(string stage)
        {
            Reads.Add(stage);
            if (stage == FailureStage) throw Failure!;
        }
        public IReadOnlyList<int>? MutationSpells(GameSession session)
        {
            Assert.Same(owner, session);
            Assert.Same(owner, SessionContext.Actual);
            Read("mutation");
            return Mutation;
        }
        public IReadOnlyList<(int Slot, int Spell)> SpellLayout(GameSession session)
        {
            Assert.Same(owner, session);
            Assert.Same(owner, SessionContext.Actual);
            Read("layout");
            return SelectLayout?.Invoke(session) ?? Layout;
        }
        public void RememberBar(GameSession session, IReadOnlyList<(int Slot, int Spell)> slots)
        {
            Assert.Same(owner, session);
            Assert.Same(owner, SessionContext.Actual);
            Read("remember");
            Remembered = slots;
        }
        public IReadOnlyList<Shortcut> GeneralBar(long characterId)
        {
            Assert.Equal(owner.CharacterId, characterId);
            Read("general");
            return General;
        }
    }

    private sealed class Recipient
    {
        internal RecordingCodec Codec { get; } = new();
        internal GameSession Session { get; }
        internal Action? AfterSend;
        internal Recipient() => Session = new(Codec, _ => { AfterSend?.Invoke(); return Task.CompletedTask; });
    }

    private sealed class RecordingCodec : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        internal int Decodes;
        public byte[] Encode(GameFrame message) { Frames.Add(message); return []; }
        public GameFrame Decode(ReadOnlySpan<byte> payload)
        {
            Decodes++;
            throw new InvalidOperationException("Unexpected decode in semantic shortcut tests");
        }
    }
}
