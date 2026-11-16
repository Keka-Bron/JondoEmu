using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// «Remordimientos de eternala» (1525) past Dlim's list: the broken crate that only wakes the
    /// araknespíritu whose drop is the spiritual cloth, and the five eternales whose powder comes
    /// off with her dust catcher alone.
    /// </summary>
    [Collection("MapManager")]
    public sealed class EternaleDustTests
    {
        private const long Character = 8_900_100_127;
        private const int Quest = 1525, Step = 2145;
        private const int FindDlim = 8684, SeeDlim = 8637, Cloth = 8680, Powder = 8682;
        private const long CrateMap = 207621134;
        private const int Crate = 519875, Araknespirit = 3758, SpiritualCloth = 15424, DustCatcher = 15422;
        private static readonly (long Map, int Element, int Item)[] Eternales =
        {
            (152337, 485527, 15419), (153363, 485528, 15602), (151826, 485529, 15603),
            (152336, 485530, 15604), (153361, 485533, 15605),
        };

        private static bool Available => File.Exists(Paths.QuestsJson) && File.Exists(Paths.WorldDb);

        private static void Clean()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterQuests WHERE CharacterId = $c; " +
                                  "DELETE FROM CharacterItems WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", Character);
            try { command.ExecuteNonQuery(); } catch (SqliteException) { }
        }

        private static bool OnTheMap(long map)
            => MobSpawnManager.GetMobsForMap(map).Any(g => g.Members.Any(m => m.Monster?.Id == Araknespirit));

        [Fact]
        public async Task The_crate_wakes_the_araknespirit_and_the_eternales_want_the_dust_catcher()
        {
            if (!Available) return;
            QuestEngine.Load();
            if (QuestEngine.Bindings.Of(Cloth) == null || QuestEngine.Bindings.Of(Powder) == null) return;
            if (MobSpawnManager.GetMonsterData(Araknespirit) == null) MobSpawnManager.InitializeAndSpawnAll();
            DatabaseManager.EnsureProgressionTables();
            Clean();
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Character, Character, CrateMap, "Eternala");
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.CharacterLevel = 130;
                    QuestEngine.LoadFrom(Character);
                    var log = QuestEngine.Log!;
                    log.Restore(Quest, Step, null, finished: false);

                    // Before Dlim has asked for the cloth, the crate is nothing.
                    Assert.False(await QuestEngine.OnInteractiveUsedAsync(pipe.ToClient, CrateMap, Crate));
                    Assert.False(OnTheMap(CrateMap));

                    log.Tick(Quest, FindDlim);
                    log.Tick(Quest, SeeDlim);
                    bool placed = await QuestEngine.OnInteractiveUsedAsync(pipe.ToClient, CrateMap, Crate);
                    if (MobSpawnManager.GetMonsterData(Araknespirit) != null)
                    {
                        Assert.True(placed);
                        Assert.True(OnTheMap(CrateMap));
                        // The objective is still the handing over, and a second press wakes no second one.
                        Assert.DoesNotContain(Cloth, log.Run(Quest)!.Done);
                        Assert.False(await QuestEngine.OnInteractiveUsedAsync(pipe.ToClient, CrateMap, Crate));
                        Assert.Single(MobSpawnManager.GetMobsForMap(CrateMap),
                                      g => g.Members.Any(m => m.Monster?.Id == Araknespirit));
                        foreach (var group in MobSpawnManager.GetMobsForMap(CrateMap)
                                     .Where(g => g.Members.Any(m => m.Monster?.Id == Araknespirit)).ToList())
                            MobSpawnManager.RemoveMobGroup(CrateMap, group.MobId);
                    }

                    // With the cloth already in the bag, the crate keeps its spirit.
                    Assert.True(await Equipment.GiveAsync(pipe.ToClient, SpiritualCloth, 1));
                    Assert.False(await QuestEngine.OnInteractiveUsedAsync(pipe.ToClient, CrateMap, Crate));
                    Assert.False(OnTheMap(CrateMap));

                    // Dlim's three objectives handed over: the eternales.
                    foreach (int handed in new[] { 8679, Cloth, 8681 }) log.Tick(Quest, handed);
                    var (map, element, item) = Eternales[0];
                    pipe.Session.State.MapId = map;
                    Assert.False(await QuestEngine.OnInteractiveUsedAsync(pipe.ToClient, map, element));
                    Assert.Equal(0, Equipment.HowMany(item));

                    Assert.True(await Equipment.GiveAsync(pipe.ToClient, DustCatcher, 1));
                    foreach (var (m, e, i) in Eternales)
                    {
                        pipe.Session.State.MapId = m;
                        Assert.True(await QuestEngine.OnInteractiveUsedAsync(pipe.ToClient, m, e));
                        Assert.Equal(1, Equipment.HowMany(i));
                    }
                    // The last of the step: the five powders end the quest.
                    Assert.True(log.Finished(Quest) || log.Run(Quest)!.Done.Contains(Powder));
                }
            }
            finally
            {
                Clean();
            }
        }

        /// <summary>
        /// «El tesoro de Totankama» (1523) wants «las 5 dosis de polvo de eternala»: the five powders
        /// above, one of each eternale. Meriana's ritual offers itself only to whoever holds all of
        /// them with the pearl and the ring, and takes one of each.
        /// </summary>
        [Fact]
        public void Merianas_ritual_takes_one_powder_of_each_eternale()
        {
            var tree = NpcDialogues.For(2205, 122421248);
            if (tree == null) return;
            var held = new[] { 15418, 15420 }.Concat(Eternales.Select(e => e.Item)).OrderBy(i => i).ToArray();

            var ritual = tree.Line(18301)?.Choice(21919);
            Assert.NotNull(ritual);
            Assert.Equal(held, ritual.TakesAll.OrderBy(i => i).ToArray());
            Assert.Equal(1, ritual.TakesCount);
            Assert.Equal(0, ritual.Takes);

            var offer = tree.Line(tree.Opening)!.Choices.Single(c => c.Reply == 21918 && c.Next == 18298);
            foreach (int item in held) Assert.Contains($"PO={item}", offer.Criterion);
            Assert.DoesNotContain("PO>", offer.Criterion);
        }
    }
}
