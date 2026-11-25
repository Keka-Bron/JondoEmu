using System;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// «Énergie renouvelable» (1572) and the Xelorium's harvests: the sundials that give minutes and
    /// rest after many uses, the old chest whose click starts a chain of written fights, and the
    /// Xelorium resources (Sepiolite, Limpet and the rest) gathered where their drawings stand.
    /// </summary>
    [Collection("MapManager")]
    public sealed class XeloriumQuestTests
    {
        private const long Chest = 144968704;
        private const int ChestElement = 486716;
        private const long Dials = 145097728;

        private static PuzzleCase[] Cases(string json) => PuzzleCase.Read(JsonDocument.Parse(json).RootElement);

        [Fact]
        public void A_case_reads_its_stacking_uses_and_rest()
        {
            var one = Cases("""{"cases":[{"gives":[{"item":15855,"count":7}],"stacks":true,"uses":5,"lockFor":300}]}""").Single();
            Assert.True(one.Stacks);
            Assert.Equal(5, one.Uses);
            Assert.Equal(300, one.LockFor);
            Assert.Equal(new[] { (15855, 7) }, one.Gives);
            var plain = Cases("""{"cases":[{"gives":[{"item":15855}]}]}""").Single();
            Assert.False(plain.Stacks);
            Assert.Equal(0, plain.Uses);
        }

        [Fact]
        public void A_stacking_case_gives_and_takes_what_it_can()
        {
            var stacks = new PuzzleCase { Stacks = true };
            var once = new PuzzleCase();
            Assert.Equal(7, stacks.GiveNow(14, 7));
            Assert.Equal(0, once.GiveNow(1, 1));
            Assert.Equal(1, once.GiveNow(0, 1));
            // Taking minutes from whoever has fewer leaves him at none; a plain take needs them all.
            Assert.Equal(2, stacks.TakeNow(2, 3));
            Assert.Equal(0, stacks.TakeNow(0, 3));
            Assert.Equal(0, once.TakeNow(2, 3));
            Assert.Equal(3, once.TakeNow(5, 3));
        }

        [Fact]
        public void An_element_rests_after_its_uses_and_comes_back()
        {
            PuzzleUses.Clear();
            var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
            const long who = 1;
            const string dial = "145097728:486746";
            for (int i = 1; i < 5; i++)
            {
                Assert.False(PuzzleUses.Use(who, dial, 5, 300, now));
                Assert.False(PuzzleUses.Locked(who, dial, now));
            }
            Assert.True(PuzzleUses.Use(who, dial, 5, 300, now));
            Assert.True(PuzzleUses.Locked(who, dial, now.AddSeconds(299)));
            // Another character, or the other dial, is not resting.
            Assert.False(PuzzleUses.Locked(2, dial, now));
            Assert.False(PuzzleUses.Locked(who, "145097728:486747", now));
            Assert.False(PuzzleUses.Locked(who, dial, now.AddSeconds(301)));
            // And the count starts over.
            Assert.False(PuzzleUses.Use(who, dial, 5, 300, now.AddSeconds(301)));
            PuzzleUses.Clear();
        }

        [Fact]
        public void A_resting_element_skips_the_case_that_counts_uses()
        {
            var cases = Cases("""{"cases":[{"gives":[{"item":15855,"count":7}],"stacks":true,"uses":3,"lockFor":60},{"message":42}]}""");
            bool locked = false;
            PuzzleCase? Pick() => PuzzleCase.First(cases, _ => true, _ => false, () => locked);
            Assert.Equal(3, Pick()!.Uses);
            locked = true;
            Assert.Equal(new[] { 42 }, Pick()!.Messages);
        }

        [Theory]
        [InlineData(486746, 249, 7)]   // the left dial, «Clock That's 7 Minutes Fast»
        [InlineData(486747, 247, 2)]   // the right one, «Clock That's 2 Minutes Fast»
        public void The_dials_of_2_6_give_their_minutes(int element, int type, int minutes)
        {
            if (MapManager.GetMapInfo(Dials) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(Dials) == null) return;
            InteractiveMessages.Forget();
            var said = InteractiveMessages.Of(Dials, element);
            Assert.NotNull(said);
            Assert.Equal(type, said!.Type);
            var dial = Assert.Single(said.Cases);
            Assert.Equal(new[] { (15855, minutes) }, dial.Gives);
            Assert.True(dial.Stacks);
            Assert.True(dial.Uses > 0 && dial.LockFor > 0);
        }

        [Fact]
        public void A_chain_is_done_when_its_last_fight_left_its_mark()
        {
            var last = new NpcFights.Fight { Marks = -1572001 };
            var first = new NpcFights.Fight { Then = last };
            Assert.Same(last, first.Final);
            Assert.False(first.Done(_ => false));
            Assert.True(first.Done(m => m == -1572001));
            // A chain with no mark is never done.
            Assert.False(new NpcFights.Fight().Done(_ => true));
        }

        [Fact]
        public void The_old_chest_starts_the_two_ambushes()
        {
            NpcFights.Forget();
            var chest = NpcFights.OnMap(Chest).SingleOrDefault(f => f.ElementId == ChestElement);
            Assert.NotNull(chest);
            Assert.Equal(241, chest!.ElementType);
            Assert.True(chest.Solo);
            Assert.True(chest.ByLevel);
            // Three waves: the first group and two more.
            Assert.Equal(2, chest.Waves.Count);
            Assert.Empty(chest.Allies);
            var second = chest.Then;
            Assert.NotNull(second);
            Assert.Empty(second!.Waves);
            Assert.Equal(4, second.Monsters.Count);
            Assert.Equal(new[] { 3883 }, second.Allies.Select(a => a.Monster));
            Assert.False(second.AlliesMustLive);
            Assert.Equal(-1572001, chest.Final.Marks);
            // Won, the chest no longer fights; and an element row never answers an NPC's reply.
            Assert.Null(NpcFights.ForElement(Chest, ChestElement, m => m == -1572001));
            Assert.Null(NpcFights.For(0, Chest, 0));
        }

        [Fact]
        public void The_next_fight_waits_for_the_one_before_to_be_won()
        {
            ScriptedFightHandler.Clear();
            var next = new NpcFights.Fight { Marks = -1572001 };
            ScriptedFightHandler.Chain(7, 100, next, Chest);
            Assert.True(ScriptedFightHandler.Waits(7, out bool ready));
            Assert.False(ready);
            // Another fight's end does not touch it.
            ScriptedFightHandler.FightEnded(7, 101, true);
            Assert.True(ScriptedFightHandler.Waits(7, out ready));
            Assert.False(ready);
            ScriptedFightHandler.FightEnded(7, 100, true);
            Assert.True(ScriptedFightHandler.Waits(7, out ready));
            Assert.True(ready);
            // Lost, the chain is over.
            ScriptedFightHandler.Chain(8, 200, next, Chest);
            ScriptedFightHandler.FightEnded(8, 200, false);
            Assert.False(ScriptedFightHandler.Waits(8, out _));
            ScriptedFightHandler.Clear();
        }

        [Theory]
        [InlineData(168558592, 508344, 291, 342, 17995)]   // Sepiolite (Miner)
        [InlineData(168558596, 508362, 290, 347, 17994)]   // Limpet (Fisherman)
        [InlineData(168558608, 508360, 290, 347, 17994)]
        [InlineData(169082880, 508278, 289, 341, 17993)]   // Quisnoa (Farmer)
        [InlineData(168820738, 508254, 287, 343, 17991)]   // Mahaquany (Lumberjack)
        [InlineData(168558594, 508370, 288, 344, 17992)]   // Salikronia (Alchemist)
        public void The_Xelorium_resources_are_gathered(long map, int element, int type, int skill, int item)
        {
            if (MapManager.GetMapInfo(map) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(map) == null) return;
            Interactives.Initialize();
            Resources.Initialize();
            Assert.True(Resources.TryGet(map, element, out var r));
            Assert.Equal((type, skill, item, 200), (r.Type, r.SkillId, r.ItemId, r.LevelMin));
        }
    }
}
