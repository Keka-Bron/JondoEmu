using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// «Dragones y chapuzonas» (1653), the way to Aguabrial: the toothed key in the Dragon Pig's
    /// throne room, the den's exit, which nobody reached -- a win over the Dragon Pig put the winners
    /// straight outside --; the clawed key on the stele of the thicket behind the Soft Oak's exit, for
    /// an abraknyde only; both keys in the keyholes of the mysterious island's sanctuary; and its
    /// pool, which brings the guardian out and then leads down to the cave.
    /// </summary>
    [Collection("MapManager")]
    public sealed class AguabrialSanctuaryTests
    {
        private const long DragonPigRoom = 72361984, ThroneRoom = 72363008, DenOutside = 72221696;
        private const long OakExit = 149428224, Thicket = 154142211, Door = 154141441, Sanctuary = 154142465, Cave = 154143489;
        private const int Porco = 784, Tree = 4167;
        private const int Melancholy = 17016, Sadness = 17017, Despair = 17018;

        private static void LoadDungeons()
        {
            if (MapManager.GetMapInfo(ThroneRoom) == null) MapManager.Initialize();
            DungeonManager.Initialize();
        }

        [Fact]
        public void Winning_the_Dragon_Pig_leads_to_its_throne_room()
        {
            LoadDungeons();
            if (DungeonManager.OfRoom(DragonPigRoom) == null) return;   // no data

            var (map, cell) = DungeonHandler.AfterAWinIn(DragonPigRoom);
            Assert.Equal(ThroneRoom, map);
            Assert.Equal(343, cell);
            Assert.False(DungeonManager.IsRoom(ThroneRoom));
        }

        [Fact]
        public void The_porco_takes_the_winners_out_of_the_throne_room()
        {
            LoadDungeons();
            if (DungeonManager.Get(6) == null) return;   // no data

            Assert.Equal(DenOutside, DungeonHandler.ExitForReply(Porco, ThroneRoom, 2864));
            Assert.Equal(0, DungeonHandler.ExitForReply(Porco, ThroneRoom, 25857));   // the key's question

            Npcs.Initialize();
            if (Npcs.TemplateOf(Porco) == null) return;
            Assert.Contains(Npcs.OnMap(ThroneRoom), n => n.NpcId == Porco);
        }

        /// <summary>
        /// One sadness more each fight against a tree of the thicket, and none off any other
        /// monster. The rows run from the last to the first: what a row rolls counts for the rows
        /// after it, and in the other order a first fight handed all three.
        /// </summary>
        [Fact]
        public void The_thicket_s_sadness_comes_from_its_trees_one_fight_at_a_time()
        {
            QuestFightDrops.Forget();
            var rows = QuestFightDrops.All.Where(d => d.Item is Melancholy or Sadness or Despair).ToList();
            if (rows.Count == 0) return;   // no content

            Assert.Equal(new[] { Despair, Sadness, Melancholy }, rows.Select(r => r.Item));
            Assert.All(rows, r =>
            {
                Assert.True(r.From(Tree));
                Assert.False(r.From(113));   // the Dragon Pig
            });
            // A row naming no monster still drops off any.
            Assert.All(QuestFightDrops.All.Where(d => d.Monsters.Count == 0), r => Assert.True(r.From(113)));
        }

        [Fact]
        public void The_keyholes_take_both_keys_and_the_stele_wants_an_abraknyde()
        {
            QuestEngine.Load();
            var keyholes = QuestEngine.Bindings.Of(9958);
            var stele = QuestEngine.Bindings.Of(9952);
            if (keyholes == null || stele == null) return;   // no content

            Assert.True(keyholes.SpendsRequired);
            Assert.Contains((16898, 1), keyholes.Requires);   // the clawed key
            Assert.Contains((16899, 1), keyholes.Requires);   // the toothed key
            Assert.Contains((17015, 1), stele.Requires);      // the transformation
            Assert.False(stele.SpendsRequired);
        }

        [Fact]
        public void The_sanctuary_opens_as_its_objectives_are_done()
        {
            if (!MapManager.WalkableCells.ContainsKey(Door)) MapManager.Initialize();
            if (MapManager.GetMapInfo(Door) == null) return;   // no world data
            Interactives.Initialize();
            TeleportManager.Initialize();

            Assert.True(TeleportManager.TryGet(Door, 490934, out var door));
            Assert.Equal(Sanctuary, door.DestinationMapId);
            Assert.Contains("Qo>9958", door.Criterion);

            Assert.True(TeleportManager.TryGet(Sanctuary, 489514, out var pool));
            Assert.Equal(Cave, pool.DestinationMapId);
            Assert.Contains("Qo>9960", pool.Criterion);

            Assert.True(TeleportManager.TryGet(OakExit, 490835, out var behindTheTree));
            Assert.Equal(Thicket, behindTheTree.DestinationMapId);
        }
    }
}
