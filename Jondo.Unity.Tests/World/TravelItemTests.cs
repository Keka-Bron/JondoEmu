using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;
using Wire = Jondo.Unity.Tests.Combat.PortalTests.Wire;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The travel items: their frames against the captures that use one, where each goes, what
    /// counts as one, and the criteria they are refused by.
    /// </summary>
    public class TravelItemTests
    {
        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        private static DatabaseManager.ItemEffectData Effect(int id, int value = 0)
            => new() { EffectId = id, Value = value };

        // ─── The frames ─────────────────────────────────────────────────────────

        /// <summary>The ankla of Anutropía's choice (kss), byte for byte «usar ankla de anutropia».</summary>
        [Fact]
        public void The_anklas_choice_is_the_captures()
        {
            var choice = TravelData.ChoiceOf(28);
            if (choice == null) return;   // no datos/travel_*.json

            Assert.Equal("0a0310ef010a05080110f0010a05080210f1010a0508031082021001181c",
                         Hex(TravelProtocol.BuildChoice(choice)));
        }

        /// <summary>The home potion's use (hpp, hpq), byte for byte «utilizar pocima de hogar».</summary>
        [Fact]
        public void The_home_potions_delay_is_the_captures()
        {
            Assert.Equal("08ee13108bbac4d8fe3318012881f092cbdd08",
                         Hex(TravelProtocol.BuildDelayedUse(2542, 1786355195147, 300000000001)));
            Assert.Equal("0881f092cbdd081001", Hex(TravelProtocol.BuildDelayFinished(300000000001)));
        }

        // ─── Where they go ──────────────────────────────────────────────────────

        /// <summary>
        /// The map references land where the captures land: Otomai's potion on 28049408, Meriana's
        /// on 134403 and the ankla's Palacio del Rey Nidas on 129499136 (each capture's jru); Bonta's
        /// and Brakmar's potions on their squares; the Temple of Alliances' potion (14447, 826 of
        /// 167) on the temple's prism square.
        /// </summary>
        [Theory]
        [InlineData(431, 28049408L, 411)]
        [InlineData(193, 134403L, 484)]
        [InlineData(670, 129499136L, 399)]
        [InlineData(189, 217318404L, 395)]
        [InlineData(282, 216530944L, 439)]
        [InlineData(167, 115081729L, 274)]
        public void A_map_reference_lands_where_the_capture_does(int reference, long map, int cell)
        {
            if (TravelData.ChoiceOf(28) == null) return;   // no datos/travel_*.json

            Assert.True(TravelData.TryMapReference(reference, out long mapId, out int cellId));
            Assert.Equal((map, cell), (mapId, cellId));
        }

        /// <summary>
        /// Anutropía's choice: its three dungeons by map reference, and «No teletransportarse» as the
        /// ankla itself, which keeps it.
        /// </summary>
        [Fact]
        public void The_anklas_options_go_to_its_dungeons_or_keep_it()
        {
            var choice = TravelData.ChoiceOf(28);
            if (choice == null) return;

            Assert.Equal(new[] { 239, 240, 241, 258 }, choice.Options.Select(o => o.Id));
            Assert.Equal(new[] { "668", "669", "670" }, choice.Options.Take(3).Select(o => o.RewardValue));
            Assert.All(choice.Options.Take(3), o => Assert.Equal(TravelData.GoToMapReference, o.RewardType));
            Assert.Equal((TravelData.KeepTheItem, "26661"), (choice.Options[3].RewardType, choice.Options[3].RewardValue));
        }

        // ─── What is a travel item ──────────────────────────────────────────────

        /// <summary>
        /// A travel item is one that only travels: the bound recall potion still is, Mr. Scuty's,
        /// which also gives an item, is not; the home potion and the guild's house and hall potions are.
        /// </summary>
        [Fact]
        public void Only_the_items_that_just_travel_are_travel_items()
        {
            Assert.Equal(600, TravelItemHandler.TravelOf(new List<DatabaseManager.ItemEffectData> { Effect(600, 1) })?.EffectId);
            Assert.Equal(600, TravelItemHandler.TravelOf(new List<DatabaseManager.ItemEffectData> { Effect(600, 1), Effect(981) })?.EffectId);
            Assert.Equal(826, TravelItemHandler.TravelOf(new List<DatabaseManager.ItemEffectData> { Effect(826, 189) })?.EffectId);
            Assert.Equal(3008, TravelItemHandler.TravelOf(new List<DatabaseManager.ItemEffectData> { Effect(3008, 28) })?.EffectId);
            Assert.Equal(830, TravelItemHandler.TravelOf(new List<DatabaseManager.ItemEffectData> { Effect(830, 5) })?.EffectId);

            Assert.Null(TravelItemHandler.TravelOf(new List<DatabaseManager.ItemEffectData> { Effect(193, 10911), Effect(826, 39) }));
            Assert.Equal(830, TravelItemHandler.TravelOf(new List<DatabaseManager.ItemEffectData> { Effect(830, 1) })?.EffectId);
            Assert.Equal(4054, TravelItemHandler.TravelOf(new List<DatabaseManager.ItemEffectData> { Effect(4054, 0) })?.EffectId);
            Assert.Null(TravelItemHandler.TravelOf(new List<DatabaseManager.ItemEffectData> { Effect(830, 3) }));
            Assert.Null(TravelItemHandler.TravelOf(new List<DatabaseManager.ItemEffectData> { Effect(110, 50) }));
        }

        // ─── Their criteria ─────────────────────────────────────────────────────

        /// <summary>
        /// An ankla's «OA=1088&amp;PL&gt;49&amp;PZ=1» without the achievement is refused for the
        /// achievement («Logro necesario», as the capture); a level short is the other refusal; the
        /// class elixirs' «Pc=11», which the client does not know either, lets them through.
        /// </summary>
        [Fact]
        public async Task The_criteria_say_which_refusal()
        {
            await using var wire = await Wire.Open(7301);
            using (SessionContext.Push(wire.Session))
            {
                wire.Session.State.CharacterLevel = 60;

                Assert.False(ItemCriteria.Holds("OA=1088&PL>49&PZ=1"));
                Assert.True(ItemCriteria.LacksOnlyAchievements("OA=1088&PL>49&PZ=1"));

                Assert.False(ItemCriteria.Holds("PL>99"));
                Assert.False(ItemCriteria.LacksOnlyAchievements("PL>99"));

                Assert.True(ItemCriteria.Holds("PZ=1&Pc=11"));
                Assert.True(ItemCriteria.Holds(""));
                Assert.False(ItemCriteria.Holds("Qf=2442&PL>39"));

                // The Multygely's: a switch no event and no quest of ours sets is let through.
                Assert.True(ItemCriteria.Holds("Pz=0&Sc!336"));
            }
        }
    }

    /// <summary>
    /// «Po!45», not in Incarnam, read off the area of the subarea one stands in. In the MapManager
    /// collection, with the other tests that load the interactives.
    /// </summary>
    [Collection("MapManager")]
    public class TravelItemAreaTests
    {
        private const long IncarnamMap = 81527303, BontaSquare = 217318404;

        [Fact]
        public async Task No_potion_works_in_Incarnam()
        {
            if (DatabaseManager.SubAreaOfMap(IncarnamMap) == 0) return;   // no map subareas in the base
            Interactives.Initialize();
            Assert.Equal(45, Interactives.AreaOfSubArea(DatabaseManager.SubAreaOfMap(IncarnamMap)));

            await using var wire = await Wire.Open(7302);
            using (SessionContext.Push(wire.Session))
            {
                wire.Session.State.MapId = IncarnamMap;
                Assert.False(ItemCriteria.Holds("Po!45"));

                wire.Session.State.MapId = BontaSquare;
                Assert.True(ItemCriteria.Holds("Po!45"));
            }
        }
    }
}
