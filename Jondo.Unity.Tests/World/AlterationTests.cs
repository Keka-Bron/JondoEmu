using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The alterations and the bonus slots: how long each lasts, how they count down, and the frames,
    /// byte for byte against the Trool fair, the guild's oracle, the gladiators and the duels.
    /// </summary>
    public class AlterationTests
    {
        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        private static AlterationCatalogue.Definition? Of(int id) => AlterationCatalogue.Of(id);

        private static DateTime At(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;

        // ─── The catalogue and how long each lasts ───────────────────────────────────────

        /// <summary>
        /// The client's own data: the Trool potion's two, one fight each, and the candies' tree, with
        /// «Perlequín» (40) and «Shigekax» (62) under «Golosina» (39).
        /// </summary>
        [Fact]
        public void The_catalogue_is_the_clients()
        {
            var almond = Of(624);
            if (almond == null) return;   // no datos/alterations_*.json

            Assert.Equal(new[] { 811, 148 }, almond.Effects.Select(e => e.Effect));
            Assert.Equal(2850, almond.Effects[1].Value);
            Assert.Equal(50, Of(625)!.Effects.Single(e => e.Effect == 174).DiceNum);

            Assert.True(AlterationCatalogue.IsCandy(799));    // «Sabor de caramelo rojo superpoderoso», 39
            Assert.True(AlterationCatalogue.IsCandy(426));    // a perlequín, 40 under 39
            Assert.True(AlterationCatalogue.IsCandy(802));    // a shigekax, 62 under 39
            Assert.False(AlterationCatalogue.IsCandy(625));   // Jibejan's initiative, 49
            Assert.False(AlterationCatalogue.IsCandy(586));   // a dragobone's compensation, 56
        }

        /// <summary>
        /// What each lasts comes from its effects: 811 fights, 817 victories, 2833 minutes ending on the
        /// minute -- the oracle set at 01:13:12.333 ends at 03:13:00, the gladiators' ten minutes at
        /// 02:00:00, as captured --, and nothing at all is until taken away.
        /// </summary>
        [Fact]
        public void How_long_it_lasts_is_in_its_effects()
        {
            if (Of(624) == null) return;

            var potion = Alterations.Start(Of(624)!, 1788305265563);
            Assert.Equal((Alterations.Kind.Fights, 1, 1), (potion.Kind, potion.Left, potion.Total));

            Assert.Equal(1788311580000, Alterations.Start(Of(859)!, 1788304392333).UntilMs);
            Assert.Equal(Alterations.Kind.Timed, Alterations.Start(Of(859)!, 1788304392333).Kind);
            Assert.Equal(1788307200000, Alterations.Start(Of(572)!, 1788306622869).UntilMs);

            Assert.Equal(Alterations.Kind.Victories, Alterations.Start(Of(102)!, 1724258718201).Kind);
            Assert.Equal(Alterations.Kind.Lasting, Alterations.Start(Of(766)!, 1783852436516).Kind);

            // An end given wins: the raid's fragments last as long as the raid.
            var fragment = Alterations.Start(Of(885)!, 1000, untilMs: 5000);
            Assert.Equal((Alterations.Kind.Timed, 5000L), (fragment.Kind, fragment.UntilMs));
        }

        // ─── The frames ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The oracle's lzs, «comprar una raid de gremio y activarlo», whole: its expiry date in Paris
        /// time (day 2, year 656, month 9, minute 13, hour 3) and its +150% experience with the 119
        /// minutes left.
        /// </summary>
        [Fact]
        public void The_oracle_is_set_as_captured()
        {
            if (Of(859) == null) return;
            var oracle = Alterations.Start(Of(859)!, 1788304392333);

            Assert.Equal("0a31088d81fef9853410db061a10120b08021090051809200d28035891161a0a3205089601107758b316" +
                         "200228e0dab4fd8534",
                         Hex(AlterationProtocol.BuildSet(oracle, At(1788304392333))));
        }

        /// <summary>The Trool potion's second lzs, «Influencia de Jibejan»: one fight left of one, +50 initiative.</summary>
        [Fact]
        public void The_trool_potion_is_set_as_captured()
        {
            if (Of(625) == null) return;
            var jibejan = Alterations.Start(Of(625)!, 1788305265563);

            Assert.Equal("0a22089ba7b3fa853410f1041a0b320608011001180158ab061a05203258ae0120032801",
                         Hex(AlterationProtocol.BuildSet(jibejan, At(1788305265563))));

            // The first, 624, carries the same two lines, the fight and the creature 2850 that
            // follows. The real server puts them in the data's order, not the reversed one the other
            // three captured come in, which only moves the lines of the tooltip.
            string almond = Hex(AlterationProtocol.BuildSet(Alterations.Start(Of(624)!, 1788305265563),
                                                           At(1788305265563)));
            Assert.Contains("1a0b320608011001180158ab06", almond);
            Assert.Contains("1a0620a2165894011", almond);
            Assert.EndsWith("20032801", almond);
        }

        /// <summary>The gladiators' fight, «feria del troll-pelea gladiadores»: a creature for ten minutes.</summary>
        [Fact]
        public void The_gladiators_follower_is_set_as_captured()
        {
            if (Of(572) == null) return;
            var follower = Alterations.Start(Of(572)!, 1788306622869);

            Assert.Equal("0a2b08959386fb853410bc041a0620d6155894011a0e120908021090051809280258911620022880b0a9fb8534",
                         Hex(AlterationProtocol.BuildSet(follower, At(1788306622869))));
        }

        /// <summary>
        /// The larva race: its fight brings «Influencia de Jibejan» to zero, and the lzo goes with the
        /// fight line at zero of one and no count left in field 5.
        /// </summary>
        [Fact]
        public void A_fight_spends_the_trool_potion_as_captured()
        {
            if (Of(625) == null) return;
            var list = new List<Alterations.Active> { Alterations.Start(Of(625)!, 1788305265563) };

            var spent = Alterations.CountDown(list, won: false);

            Assert.Single(spent);
            Assert.Equal("0a1e089ba7b3fa853410f1041a0932041001180158ab061a05203258ae012003",
                         Hex(AlterationProtocol.BuildLost(spent[0], At(1788305265563))));
        }

        /// <summary>
        /// The duels' lzo: the «Misión» 102 with its one victory left, and the lasting 766 with nothing
        /// but its kind.
        /// </summary>
        [Fact]
        public void The_duels_losses_are_as_captured()
        {
            if (Of(102) == null) return;
            var now = At(1788305265563);

            Assert.Equal("0a1a08f9a3d0ae973210661a0b320608011001180158b10620042801",
                         Hex(AlterationProtocol.BuildLost(Alterations.Start(Of(102)!, 1724258718201), now)));
            Assert.Equal("0a0c08a49890aff53310fe052001",
                         Hex(AlterationProtocol.BuildLost(Alterations.Start(Of(766)!, 1783852436516), now)));
        }

        /// <summary>The list (lzp) is each entry in f1, and goes empty without any, as at the end of the class captures.</summary>
        [Fact]
        public void The_list_is_every_entry()
        {
            Assert.Empty(AlterationProtocol.BuildList(Array.Empty<Alterations.Active>(), DateTime.UtcNow));
            if (Of(766) == null) return;

            var now = At(1788305265563);
            var lasting = Alterations.Start(Of(766)!, 1783852436516);
            var potion = Alterations.Start(Of(625)!, 1788305265563);
            Assert.Equal("0a0c08a49890aff53310fe052001", Hex(AlterationProtocol.BuildList(new[] { lasting }, now)));
            Assert.Equal(Hex(AlterationProtocol.BuildSet(lasting, now)) + Hex(AlterationProtocol.BuildSet(potion, now)),
                         Hex(AlterationProtocol.BuildList(new[] { lasting, potion }, now)));
        }

        /// <summary>
        /// The world entry's lzp is the character's, not the capture's: the recorded one carried the
        /// capturer's 763, 764, 766 and 102, and every character entered with them. One with none
        /// enters with an empty list.
        /// </summary>
        [Fact]
        public void The_world_entry_lists_the_characters_own()
        {
            ForgetAlterations();
            try
            {
                // The recorded lzp: 764 and 763, lasting, as the manifest has them.
                byte[] recorded = ConnectionProtocol.Push(Jondo.Unity.Protocol.Op.Lzp,
                    Convert.FromHexString("0a0c08e4d395aff53310fc0520010a0c08e4d395aff53310fb052001"));
                var character = new DatabaseManager.DbCharacter { Id = Eater };

                Assert.Equal(Hex(ConnectionProtocol.Push(Jondo.Unity.Protocol.Op.Lzp, Array.Empty<byte>())),
                             Hex(WorldEntry.Rebuilt(recorded, "", character)!));

                if (Of(625) == null) return;
                var now = DateTime.UtcNow;
                var jibejan = Alterations.Gain(Eater, 625, now)!;
                Assert.Equal(Hex(ConnectionProtocol.Push(Jondo.Unity.Protocol.Op.Lzp, AlterationProtocol.BuildList(new[] { jibejan }, now))),
                             Hex(WorldEntry.Rebuilt(recorded, "", character)!));
            }
            finally
            {
                ForgetAlterations();
            }
        }

        // ─── Counting down ─────────────────────────────────────────────────────────────

        /// <summary>Fights count every fight; victories only the ones won; the rest never.</summary>
        [Fact]
        public void Fights_count_always_and_victories_when_won()
        {
            var fights = new Alterations.Active { Id = 1, Kind = Alterations.Kind.Fights, Left = 2, Total = 2 };
            var wins = new Alterations.Active { Id = 2, Kind = Alterations.Kind.Victories, Left = 1, Total = 1 };
            var timed = new Alterations.Active { Id = 3, Kind = Alterations.Kind.Timed, UntilMs = 10 };
            var list = new List<Alterations.Active> { fights, wins, timed };

            Assert.Empty(Alterations.CountDown(list, won: false));
            Assert.Equal((1, 1), (fights.Left, wins.Left));

            Assert.Equal(new[] { 1, 2 }, Alterations.CountDown(list, won: true).Select(a => a.Id));
            Assert.Equal((0, 0), (fights.Left, wins.Left));

            Assert.Empty(Alterations.Due(list, 9));
            Assert.Equal(3, Alterations.Due(list, 10).Single().Id);
        }

        // ─── The bonus slots ───────────────────────────────────────────────────────────

        /// <summary>
        /// A boost food goes to 21, a blessing to 22 or 23, a follower to 27 (SlotLooks); the items
        /// that are no slot's go to the bag.
        /// </summary>
        [Fact]
        public void Each_slot_takes_its_type()
        {
            Assert.Equal(new[] { 21 }, BonusSlots.SlotsFor(BonusSlots.BoostFoodType));
            Assert.Equal(new[] { 22, 23 }, BonusSlots.SlotsFor(BonusSlots.BlessingType));
            Assert.Equal(new[] { 27 }, BonusSlots.SlotsFor(32));
            Assert.True(BonusSlots.IsSlotType(32));
            Assert.False(BonusSlots.IsSlotType(42));

            Assert.True(Equipment.CountsOnSheet(21));
            Assert.True(Equipment.CountsOnSheet(15));
            Assert.False(Equipment.CountsOnSheet(19));
            Assert.False(Equipment.CountsOnSheet(Equipment.Bag));
        }

        /// <summary>
        /// The tournament character's blessing, slot 22: it enters the world at 665 fights of 666 and,
        /// one fight on, the iuj of the class captures carries 664 -- byte for byte.
        /// </summary>
        [Fact]
        public void A_blessing_counts_down_as_captured()
        {
            var blessing = new Equipment.Item { Uid = 33932659, Template = 17545, Position = 22, Quantity = 1 };
            blessing.Effects.AddRange(new[]
            {
                new Equipment.ItemEffect(111, 1, 0, 0),
                new Equipment.ItemEffect(128, 1, 0, 0),
                new Equipment.ItemEffect(117, 1, 0, 0),
                new Equipment.ItemEffect(811, 665, 666, 666),
            });

            var (effects, spent) = BonusSlots.CountDown(blessing.Effects, won: false)!.Value;
            Assert.False(spent);
            blessing.Effects.Clear();
            blessing.Effects.AddRange(effects);

            Assert.Equal("0a3208162a2e0889890112042001586f12052001588001120420015875120e3209089805109a05189a0558ab06" +
                         "180120f38a9710",
                         Hex(AlterationProtocol.BuildSlotItemChanged(blessing)));
        }

        /// <summary>A candy's boost food is placed with its fights as left, total, total, and goes at zero.</summary>
        [Fact]
        public void A_boost_food_starts_full_and_goes_at_zero()
        {
            // «Efecto de caramelo de menta de pequeño aprendiz»: 25 wisdom, ten fights.
            var fresh = BonusSlots.Fresh(new[] { new Equipment.ItemEffect(124, 25, 0, 0), new Equipment.ItemEffect(811, 10, 0, 0) });
            Assert.Equal((25L, 0L, 0L), (fresh[0].Value, fresh[0].DiceNum, fresh[0].DiceSide));
            Assert.Equal((10L, 10L, 10L), (fresh[1].Value, fresh[1].DiceNum, fresh[1].DiceSide));

            var last = BonusSlots.CountDown(new[] { new Equipment.ItemEffect(811, 1, 10, 10) }, won: true)!.Value;
            Assert.True(last.Spent);

            // Nothing that counts, nothing counted: a slot item with no 811 stays as it is.
            Assert.Null(BonusSlots.CountDown(new[] { new Equipment.ItemEffect(124, 25, 0, 0) }, won: true));
            // Victories only when won.
            Assert.Null(BonusSlots.CountDown(new[] { new Equipment.ItemEffect(817, 3, 3, 3) }, won: false));
        }

        // ─── The items ─────────────────────────────────────────────────────────────────

        private static DatabaseManager.ItemEffectData Effect(int id, int dice = 0, int side = 0, int value = 0)
            => new() { EffectId = id, DiceNum = dice, DiceSide = side, Value = value };

        /// <summary>
        /// The Trool potion sets two alterations, a dragobone one; a candy gives its boost food, a bag
        /// of supercandies twenty candies; what does more than that is someone else's.
        /// </summary>
        [Fact]
        public void What_each_item_gives()
        {
            Assert.True(BonusItemHandler.GivesAlterations(new[] { Effect(2892, 624), Effect(2892, 625) }));
            Assert.True(BonusItemHandler.GivesAlterations(new[] { Effect(2892, 586), Effect(983) }));
            Assert.True(BonusItemHandler.GivesAlterations(new[] { Effect(2892, 50), Effect(2893, 51), Effect(2893, 52) }));
            Assert.False(BonusItemHandler.GivesAlterations(new[] { Effect(2892, 526), Effect(222, 1, 0, 317) }));

            Assert.Equal(new[] { (1723, 1) }, BonusItemHandler.GivenItems(new[] { Effect(193, 1723) }));
            Assert.Equal(new[] { (11693, 1) }, BonusItemHandler.GivenItems(new[] { Effect(209, 0, 1, 11693), Effect(981) }));
            Assert.Equal(new[] { (21627, 20) }, BonusItemHandler.GivenItems(new[] { Effect(209, 0, 20, 21627) }));
            Assert.Null(BonusItemHandler.GivenItems(new[] { Effect(193, 2062), Effect(949, 0, 0, 1) }));
            Assert.Null(BonusItemHandler.GivenItems(new[] { Effect(110, 30) }));

            // The precious ore shard on its way out of the sewers: the next shard and the quest's
            // marker, itself used up, and what the client shows of it.
            var shard = new[]
            {
                Effect(209, 0, 1, 26200), Effect(350, 0, 0, 6625), Effect(350, 0, 0, 6626),
                Effect(192, 26203), Effect(209, 0, 1, 26204), Effect(630, 0, 0, 1437),
            };
            Assert.Equal(new[] { (26200, 1), (26204, 1) }, BonusItemHandler.GivenItems(shard, 26203));
            // Using up some other item is not something it does.
            Assert.Null(BonusItemHandler.GivenItems(new[] { Effect(209, 0, 1, 19135), Effect(192, 19134) }, 10530));
        }

        // ─── Kept in the base ──────────────────────────────────────────────────────────

        private const long Eater = 8_950_000_201;

        private static void ForgetAlterations()
        {
            Alterations.Forget(Eater);
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterAlterations WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", Eater);
            try { command.ExecuteNonQuery(); } catch (SqliteException) { }
        }

        /// <summary>
        /// What a character has is kept per character in the base: read again after forgetting it, the
        /// fight counted is still counted, the timed one still ends when it did, and what was lost or
        /// spent is gone. The initiative of Jibejan adds up while it lasts.
        /// </summary>
        [Fact]
        public void Alterations_are_kept_in_the_base()
        {
            if (Of(625) == null) return;
            ForgetAlterations();
            try
            {
                var now = DateTime.UtcNow;
                Assert.NotNull(Alterations.Gain(Eater, 625, now));
                Assert.NotNull(Alterations.Gain(Eater, 859, now));
                Assert.NotNull(Alterations.Gain(Eater, 102, now));
                Assert.True(Alterations.Has(Eater, 625));
                if (EffectTable.TryGet(174, out var initiative))
                    Assert.Equal(50, Alterations.BonusOf(Eater, initiative.Characteristic));

                // Forgotten and read again: the same three.
                Alterations.Forget(Eater);
                Assert.Equal(new[] { 102, 625, 859 }, Alterations.Of(Eater).Select(a => a.Id).OrderBy(i => i));

                // A fight lost: Jibejan's one fight is spent, the victory is not counted.
                var (listed, spent) = Alterations.CountFight(Eater, won: false);
                Assert.Equal(3, listed.Count);
                Assert.Equal(625, spent.Single().Id);
                Alterations.Forget(Eater);
                Assert.False(Alterations.Has(Eater, 625));
                Assert.Equal(1, Alterations.Of(Eater).Single(a => a.Id == 102).Left);

                // The oracle's two hours are a date, kept as such: still there now, gone after it.
                long until = Alterations.Of(Eater).Single(a => a.Id == 859).UntilMs;
                Assert.Empty(Alterations.Expire(Eater, now));
                Assert.Equal(859, Alterations.Expire(Eater, At(until)).Single().Id);
                Alterations.Forget(Eater);
                Assert.False(Alterations.Has(Eater, 859));

                Assert.NotNull(Alterations.Lose(Eater, 102));
                Alterations.Forget(Eater);
                Assert.Empty(Alterations.Of(Eater));
            }
            finally
            {
                ForgetAlterations();
            }
        }
    }
}
