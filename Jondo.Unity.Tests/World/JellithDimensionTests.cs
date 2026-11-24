using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Jellith Dimension as the game has it (Dofus pour les noobs, «Gelaxième Dimension»): the
    /// Multygely takes the party into the First Room, four rooms of four groups -- one per majority
    /// colour -- each win leaving a puddle of the colour beaten, then the Fifth Room, where the
    /// royals send the party into the room the puddles open, and from it to the Exit room.
    /// </summary>
    [Collection("MapManager")]
    public sealed class JellithDimensionTests
    {
        private const int Dimension = 24, Multygely = 996;
        private const long Outside = 88084997, First = 98566657, Second = 98567681, Third = 98566659, Fourth = 98567683,
                           Hall = 98568707, ExitRoom = 98568705,
                           BlueRoom = 98566661, MintRoom = 98567685, StrawberryRoom = 98568709, LemonRoom = 98566663,
                           JelliesRoom = 98567687;
        private const int Blue = 55, Mint = 56, Strawberry = 57, Lemon = 429;

        private static void Load()
        {
            if (MapManager.GetMapInfo(Hall) == null) MapManager.Initialize();
            DungeonManager.Initialize();
        }

        [Fact]
        public void The_run_is_four_rooms_then_the_hall_then_a_royal_room_then_the_exit()
        {
            Load();
            var dimension = DungeonManager.Get(Dimension)!;
            Assert.Equal(new[] { First, Second, Third, Fourth }, dimension.Rooms);
            Assert.Empty(dimension.Bosses);
            Assert.Equal(Second, DungeonHandler.AfterAWinIn(First).Map);
            Assert.Equal(Third, DungeonHandler.AfterAWinIn(Second).Map);
            Assert.Equal(Fourth, DungeonHandler.AfterAWinIn(Third).Map);
            Assert.Equal(Hall, DungeonHandler.AfterAWinIn(Fourth).Map);
            Assert.Equal(Hall, DungeonManager.ChoiceRoom(dimension));
            Assert.False(DungeonManager.IsRoom(Hall));
            Assert.False(DungeonManager.IsRoom(ExitRoom));

            foreach (var (room, bosses) in new[]
                     {
                         (BlueRoom, new[] { 58 }), (MintRoom, new[] { 85 }), (StrawberryRoom, new[] { 86 }),
                         (LemonRoom, new[] { 430 }), (JelliesRoom, new[] { 58, 430, 86, 85 }),
                     })
            {
                var royal = DungeonManager.OfRoom(room)!;
                Assert.Equal(bosses, royal.Bosses);
                Assert.Same(dimension, DungeonManager.ChosenFrom(royal));
                Assert.Equal(ExitRoom, DungeonHandler.AfterAWinIn(room).Map);
                Assert.Equal(0, DungeonHandler.RecordFightProgress(0, room, won: true));
                Assert.Same(dimension, DungeonManager.ColouredTo(room));
            }
            Assert.Null(DungeonManager.ColouredTo(Hall));
            Assert.Null(DungeonManager.ColouredTo(First));
        }

        /// <summary>«il suffit de consommer un Multygely pour être téléporté dans le donjon».</summary>
        [Fact]
        public void The_multygely_is_the_way_into_the_first_room()
        {
            Load();
            Assert.Equal(Dimension, DungeonManager.KeyedInto(Multygely, First)?.Id);
            Assert.Null(DungeonManager.KeyedInto(Multygely, Second));
            Assert.Null(DungeonManager.KeyedInto(Multygely + 1, First));
            Assert.Equal(new[] { (Multygely, 1) }, DungeonManager.Get(Dimension)!.Required.Select(r => (r.Item, r.Count)));

            // The item itself: one teleport (826) whose map reference is the First Room.
            var travel = TravelItemHandler.TravelOf(DatabaseManager.GetItemEffectsData(
                DatabaseManager.GetItemTemplatePossibleEffects(Multygely)));
            if (travel == null) return;   // no item effects in the base
            Assert.Equal(826, travel.EffectId);
            if (TravelData.TryMapReference(travel.Value, out long map, out _)) Assert.Equal(First, map);
        }

        /// <summary>
        /// «Une fois le combat terminé, vous obtiendrez en inventaire une flaque de gelée de la couleur
        /// de la gelée majoritaire»; «Pour affronter les 4 Gelées Royales en même temps, il faudra avoir
        /// une flaque de chaque couleur de Gelée».
        /// </summary>
        [Fact]
        public void The_puddles_choose_the_royal_room()
        {
            Load();
            var rule = DungeonManager.Get(Dimension)!.Colours!;

            Assert.Equal(Mint, DungeonPuddles.Majority(rule, new Dictionary<int, int> { [Mint] = 5, [Blue] = 1, [Lemon] = 1, [Strawberry] = 1 }));
            Assert.Null(DungeonPuddles.Majority(rule, new Dictionary<int, int> { [Mint] = 2, [Blue] = 2 }));
            Assert.Null(DungeonPuddles.Majority(rule, new Dictionary<int, int> { [58] = 1 }));

            Assert.Empty(DungeonPuddles.Choice(rule, new Dictionary<int, int>()));
            Assert.Equal(new HashSet<long> { LemonRoom },
                         DungeonPuddles.Choice(rule, new Dictionary<int, int> { [Lemon] = 3, [Blue] = 1 }));
            Assert.Equal(new HashSet<long> { BlueRoom, StrawberryRoom },
                         DungeonPuddles.Choice(rule, new Dictionary<int, int> { [Blue] = 2, [Strawberry] = 2 }));
            Assert.Equal(new HashSet<long> { BlueRoom, MintRoom, StrawberryRoom, LemonRoom, JelliesRoom },
                         DungeonPuddles.Choice(rule, new Dictionary<int, int> { [Blue] = 1, [Mint] = 1, [Strawberry] = 1, [Lemon] = 1 }));
            // One of each colour opens the four together whatever else is held.
            Assert.Equal(new HashSet<long> { MintRoom, JelliesRoom },
                         DungeonPuddles.Choice(rule, new Dictionary<int, int> { [Blue] = 1, [Mint] = 2, [Strawberry] = 1, [Lemon] = 1 }));

            // Anywhere else nothing is closed.
            Assert.True(DungeonPuddles.Opens(0, Hall));
            Assert.True(DungeonPuddles.Opens(0, Outside));
        }

        /// <summary>
        /// «Dans chaque salle du donjon, il y a 4 groupes. Chaque groupe est composé de manière différente
        /// afin qu'il y ait un type de gelée majoritaire dans chacun des groupes».
        /// </summary>
        [Fact]
        public void Each_room_has_a_group_of_each_majority_colour()
        {
            Load();
            var colours = new[] { Blue, Mint, Strawberry, Lemon };
            for (int k = 0; k < colours.Length; k++)
            {
                var roster = MobSpawnManager.ColourRoster(colours, k);
                for (int size = 4; size <= roster.Count; size++)
                {
                    var counts = roster.Take(size).GroupBy(m => m).ToDictionary(g => g.Key, g => g.Count());
                    Assert.Equal(colours[k], counts.OrderByDescending(c => c.Value).First().Key);
                    Assert.True(counts.Where(c => c.Key != colours[k]).All(c => c.Value < counts[colours[k]]));
                }
            }

            MobSpawnManager.EnsureMonsterData();
            var dimension = DungeonManager.Get(Dimension)!;
            for (int index = 0; index < dimension.Rooms.Count; index++)
            {
                var groups = MobSpawnManager.ComposeColourRoom(dimension, index, k => -1 - k);
                Assert.Equal(colours, groups.Select(g => g.Members[0].Monster.Id));
                Assert.Equal(groups.Count, groups.Select(g => g.CellId).Distinct().Count());
                Assert.All(groups, g => Assert.True(g.Modular));
            }
        }

        [Fact]
        public void The_summoning_of_jelly_weakens_only_the_summoned_ones()
        {
            const string mask = "C,*i";  // spell 32732, effect 1033 (-20% vitality)
            var placed = new Jondo.Unity.World.Fights.Fighter { IsMonster = true, MonsterId = 55 };
            var summoned = new Jondo.Unity.World.Fights.Fighter { IsMonster = true, MonsterId = 55, Invocador = -1 };
            Assert.False(EffectEngine.CasterQualifies(placed, mask));
            Assert.True(EffectEngine.CasterQualifies(summoned, mask));

            // Next to other starred kinds the kinds are alternatives and let it through.
            Assert.True(EffectEngine.CasterQualifies(placed, "C,*h,*m,*d,*i"));
        }
    }
}
