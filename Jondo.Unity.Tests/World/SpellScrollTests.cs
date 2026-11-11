using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The spell scrolls and the special spells they teach: which spell each one teaches, that a
    /// character has only the ones he learnt, how they travel, and that they are kept.
    /// </summary>
    public class SpellScrollTests
    {
        private const int Llamita = 24006;
        private const int Llamilla = 350;
        private const int CapturaDeAlmas = 413;

        /// <summary>
        /// Each of the thirteen scrolls teaches the base of the pair its criterion names -- Llamita's
        /// level, 28324, is «Atracción de Balí»'s in this client and would teach the wrong spell -- and
        /// Captura de Almas's, with no criterion, the spell of its level.
        /// </summary>
        [Fact]
        public void Each_scroll_teaches_its_spell()
        {
            if (!SpellTable.IsLoaded || DatabaseManager.GetItemTemplateType(718) == 0) return;

            var expected = new Dictionary<int, int>
            {
                [718] = Llamita, [719] = 368, [720] = 369, [721] = 370, [9200] = 373, [9201] = 367,
                [10603] = CapturaDeAlmas, [10604] = 366, [10605] = 364, [14048] = 3506,
                [27415] = 24011, [27416] = 24015, [29512] = 18646,
            };
            foreach (var (scroll, spell) in expected)
                Assert.True(spell == LearnedSpells.TaughtBy(scroll), $"scroll {scroll} teaches {LearnedSpells.TaughtBy(scroll)}, not {spell}");

            Assert.Equal(expected.Values.OrderBy(s => s), LearnedSpells.Everything.OrderBy(s => s));
        }

        /// <summary>The criterion rules; with none, the level's spell, unless it is a class's.</summary>
        [Fact]
        public void The_criterion_names_the_spell_before_the_level()
        {
            if (!SpellTable.IsLoaded) return;
            Assert.Equal(Llamita, LearnedSpells.Resolve("PT!24006&PT!350", 28324, _ => 6958));
            Assert.Equal(Llamita, LearnedSpells.Resolve("PT!350", 0, _ => 0));
            Assert.Equal(CapturaDeAlmas, LearnedSpells.Resolve("", 2063, _ => CapturaDeAlmas));
            Assert.Equal(0, LearnedSpells.Resolve("", 0, _ => 0));
        }

        /// <summary>
        /// Without scrolls, no special spell; with Llamita learnt, its pair from level 30; Captura de
        /// Almas on its own; and the koliseo's bots, which ask with nothing, every common pair.
        /// </summary>
        [Fact]
        public void A_character_has_the_special_spells_he_learnt()
        {
            if (!SpellTable.IsLoaded) return;
            const int Yopuka = 8;

            Assert.DoesNotContain(SpellTable.KnownFor(Yopuka, 200, null, Array.Empty<int>()), k => k.Special);

            var learnt = new[] { Llamita, CapturaDeAlmas };
            Assert.DoesNotContain(SpellTable.KnownFor(Yopuka, 20, null, learnt), k => k.SpellId == Llamita);
            var at200 = SpellTable.KnownFor(Yopuka, 200, null, learnt).Where(k => k.Special).ToList();
            Assert.Equal(new[] { Llamita, CapturaDeAlmas }, at200.Select(k => k.SpellId));
            Assert.Equal(3, at200[0].Grade);
            Assert.Equal(-CapturaDeAlmas, at200[1].PairId);

            // The variant chosen travels instead, as the captured account's Llamilla.
            var chosen = new Dictionary<int, int> { [SpellTable.PairOf(Llamita)!.Id] = Llamilla };
            Assert.Contains(SpellTable.KnownFor(Yopuka, 200, chosen, learnt), k => k.SpellId == Llamilla && k.Special);

            Assert.Equal(SpellTable.CommonPairs.Count, SpellTable.KnownFor(Yopuka, 200).Count(k => k.Special));
        }

        /// <summary>A pair learnt holds both its spells; the criterion's «PT!» asks about either.</summary>
        [Fact]
        public void A_pair_learnt_holds_both_halves()
        {
            if (!SpellTable.IsLoaded) return;
            Assert.True(LearnedSpells.Holds(new[] { Llamita }, Llamilla));
            Assert.True(LearnedSpells.Holds(new[] { Llamita }, Llamita));
            Assert.False(LearnedSpells.Holds(new[] { Llamita }, 368));
            Assert.True(LearnedSpells.Holds(new[] { CapturaDeAlmas }, CapturaDeAlmas));
        }

        /// <summary>A scroll is effect 604 and nothing besides.</summary>
        [Fact]
        public void A_scroll_is_what_teaches_and_nothing_else()
        {
            static DatabaseManager.ItemEffectData E(int id) => new() { EffectId = id };
            Assert.True(SpellItemHandler.TeachesASpell(new[] { E(604) }));
            Assert.True(SpellItemHandler.TeachesASpell(new[] { E(604), E(981) }));
            Assert.False(SpellItemHandler.TeachesASpell(new[] { E(2864), E(1175) }));
            Assert.False(SpellItemHandler.TeachesASpell(new[] { E(604), E(110) }));
        }

        private const long Reader = 8_950_000_301;

        private static void ForgetSpells()
        {
            LearnedSpells.Forget(Reader);
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterLearnedSpells WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", Reader);
            try { command.ExecuteNonQuery(); } catch (SqliteException) { }
        }

        /// <summary>
        /// Learnt and kept: read again from the base it is still there, the spell list carries it with
        /// the special-spell type, Liberación at its third grade,
        /// and Llamita's scroll is refused by its criterion once its pair is known.
        /// </summary>
        [Fact]
        public void A_learnt_spell_is_kept_and_listed()
        {
            if (!SpellTable.IsLoaded) return;
            ForgetSpells();
            var session = GameSession.SinSocket();
            try
            {
                using (SessionContext.Push(session))
                {
                    session.State.CharacterId = Reader;
                    session.State.Breed = 8;
                    session.State.CharacterLevel = 200;

                    Assert.True(ItemCriteria.Holds("PT!24006&PT!350"));
                    Assert.True(LearnedSpells.Learn(Reader, Llamita));
                    Assert.False(LearnedSpells.Learn(Reader, Llamita));
                    Assert.True(LearnedSpells.Learn(Reader, 368));
                    Assert.False(ItemCriteria.Holds("PT!24006&PT!350"));
                    Assert.True(ItemCriteria.Holds("PT!369&PT!24017"));

                    LearnedSpells.Forget(Reader);
                    Assert.Equal(new[] { 368, Llamita }, LearnedSpells.Of(Reader).OrderBy(s => s));

                    var spells = SpellMessages.Live.Snapshot(session);
                    Assert.Equal(new SpellItem(3, 368, SpellType.Unknown2),
                        Assert.Single(spells.HumanSpells, spell => spell.SpellId == 368));
                    Assert.Contains(spells.HumanSpells, spell => spell.SpellId == Llamita && spell.SpellType == SpellType.Unknown2);
                }
            }
            finally
            {
                ForgetSpells();
            }
        }
    }
}
