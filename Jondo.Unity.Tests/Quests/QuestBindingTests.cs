using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// What an objective asks for, and the bindings that close the ones the catalogue is silent on.
    /// </summary>
    public class QuestBindingTests
    {
        private static QuestObjective Objective(int type, params int[] parameters)
            => new QuestObjective { Id = 1, StepId = 1, TypeId = type, Parameters = parameters };

        [Fact]
        public void Bringing_and_showing_read_the_item_out_of_parameter_one()
        {
            // The layout is measured, not assumed: parameter0 of types 2 and 3 is a real
            // NpcTemplates id 320 of 323 and 2,143 of 2,146 times, so the item cannot be there.
            // Reading it blind would send the player looking for an item numbered like an NPC.
            var bring = Objective(QuestCatalogue.BringItems, 119, 1746, 3);
            Assert.Equal(119, bring.NpcId);
            Assert.Equal(1746, bring.ItemId);
            Assert.Equal(3, bring.ItemCount);
            Assert.True(bring.ConsumesItems);

            var show = Objective(QuestCatalogue.ShowItems, 739, 1501, 1);
            Assert.Equal(739, show.NpcId);
            Assert.Equal(1501, show.ItemId);
            Assert.Equal(1, show.ItemCount);

            // The whole difference between the two types. Showing a ring and handing it over are
            // not the same thing, and getting it backwards either robs the player or lets one
            // objective be finished over and over with the same items.
            Assert.False(show.ConsumesItems);
        }

        [Fact]
        public void Crafting_reads_the_item_out_of_parameter_zero_instead()
        {
            // A different family with a different layout — [item, count] rather than
            // [npc, item, count] — which is why ItemId asks the type first.
            var craft = Objective(QuestCatalogue.CraftItem, 9953, 2);
            Assert.Equal(9953, craft.ItemId);
            Assert.Equal(2, craft.ItemCount);
            Assert.Equal(0, craft.NpcId);
        }

        [Fact]
        public void An_objective_that_wants_no_item_says_so()
        {
            // Zero and not "whatever is in parameter1". A talk objective's parameter1 is nothing in
            // particular, and returning it would make the server demand an item that does not exist
            // before it would let the player say hello.
            var talk = Objective(1, 447);
            Assert.Equal(0, talk.ItemId);
            Assert.Equal(0, talk.ItemCount);

            var kill = Objective(QuestCatalogue.BeatInOneFight, 182, 5);
            Assert.Equal(0, kill.ItemId);
        }

        [Fact]
        public void Discovering_a_map_reads_the_map_and_never_parameter_zero()
        {
            // parameter0 of type 4 is a TEXT id, not a map: all 874 of them resolve to a place
            // name — "Laboratoire Wabbit", "Souterrain de la Bibliothèque" — and none is a map id.
            // Taking it as a map would compare a text key against the map the player is standing
            // on and never match, or worse, match one by accident.
            var discover = new QuestObjective
            {
                Id = 1,
                TypeId = QuestCatalogue.DiscoverMap,
                MapId = 185862149,
                Parameters = new[] { 287243 },
            };

            Assert.Equal(185862149, discover.DiscoverMapId);

            // 109 of the 874 name the map only as text. Those stay open rather than being closed
            // on a guess.
            var nameless = new QuestObjective
            {
                Id = 2,
                TypeId = QuestCatalogue.DiscoverMap,
                Parameters = new[] { 287243 },
            };

            Assert.Equal(0, nameless.DiscoverMapId);
        }

        [Fact]
        public void Discovering_an_area_reads_the_subarea()
        {
            var area = Objective(QuestCatalogue.DiscoverArea, 165);
            Assert.Equal(165, area.DiscoverAreaId);

            // And nothing else claims to be a subarea: a talk objective's NPC is not one.
            Assert.Equal(0, Objective(1, 165).DiscoverAreaId);
        }

        [Fact]
        public void A_binding_is_reachable_by_objective_and_by_element()
        {
            var book = new QuestBindingBook();
            book.Add(new QuestBinding
            {
                ObjectiveId = 9820,
                QuestId = 1640,
                Elements = new List<(long, int)> { (153355272, 504674), (153355264, 504676) },
            });

            Assert.NotNull(book.Of(9820));
            Assert.Equal(2, book.ElementCount);

            // Either stele closes it. The objective says "un vestige", not "le vestige", and there
            // are two of them in the pastures — binding one would send whoever clicked the other
            // across the subarea with nothing to tell them why.
            Assert.Single(book.At(153355272, 504674));
            Assert.Single(book.At(153355264, 504676));
            Assert.Empty(book.At(153355272, 504676));

            Assert.Single(book.OnMap(153355272));
        }

        [Fact]
        public void A_row_that_says_nothing_about_type_gets_minus_one_and_not_zero()
        {
            // The one field where "not written" and "written as zero" cannot be told apart by
            // falling back on default(int): the measured default here is -1, and 0 is a legitimate
            // interactive type. Reading it the usual way would silently retype every quest element.
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
            File.WriteAllText(path, """
            { "bindings": [
              { "objective": 1, "quest": 2, "elements": [ {"map": 3, "element": 4} ] },
              { "objective": 5, "quest": 6, "type": 0, "elements": [ {"map": 7, "element": 8} ] }
            ] }
            """);

            try
            {
                var book = QuestBindingContent.Load(path);
                Assert.Equal(-1, book.Of(1)!.TypeId);
                Assert.Equal(0, book.Of(5)!.TypeId);
                Assert.Equal(QuestBinding.DefaultSkill, book.Of(1)!.SkillId);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Every_binding_written_closes_a_real_objective_of_the_quest_it_names()
        {
            // The content, not the loader. A binding naming an objective that does not exist does
            // nothing for ever and says nothing; one naming the wrong quest makes the server look
            // up a run the player does not have, which looks exactly the same.
            string path = Paths.ContentFile(QuestBindingContent.AuthoredFile);
            if (!File.Exists(path) || !File.Exists(Paths.QuestsJson)) return;

            var book = new QuestCatalogue();
            if (!book.Ready) return;

            var bindings = QuestBindingContent.Load(path);
            var wrong = new List<string>();

            foreach (var quest in book.All())
            {
                foreach (var step in quest.Steps)
                {
                    foreach (var objective in step.Objectives)
                    {
                        var binding = bindings.Of(objective.Id);
                        if (binding == null) continue;

                        if (binding.QuestId != quest.Id)
                        {
                            wrong.Add($"objective {objective.Id} is bound to quest {binding.QuestId}, " +
                                      $"but belongs to {quest.Id} \"{quest.Name}\"");
                        }

                        // Binding one the engine already closes gives it a second way to finish,
                        // which is how a player ends up clicking a stele to hand in five ortie.
                        if (objective.TypeId != QuestCatalogue.FreeText)
                        {
                            wrong.Add($"objective {objective.Id} is of type {objective.TypeId}, " +
                                      "which the engine already closes on its own");
                        }

                        // Each kind carries a different thing, and a row missing its own is a row
                        // that does nothing at run time without a word of complaint.
                        string missing = binding.Kind switch
                        {
                            QuestBindingKind.Click when binding.Elements.Count == 0 => "no element",
                            QuestBindingKind.Talk when binding.NpcId == 0 => "no npc",
                            QuestBindingKind.Enter when binding.MapId == 0 => "no map",
                            QuestBindingKind.Beat when binding.MonsterId == 0 => "no monster",
                            _ => "",
                        };

                        if (missing.Length > 0)
                        {
                            wrong.Add($"objective {objective.Id} is bound as {binding.Kind} with {missing}");
                        }

                        // The one an "enter" row cannot get wrong quietly: the objective carries
                        // the map itself, so a row naming a different one was copied wrong.
                        if (binding.Kind == QuestBindingKind.Enter && objective.MapId != 0
                            && binding.MapId != objective.MapId)
                        {
                            wrong.Add($"objective {objective.Id} is bound to map {binding.MapId}, " +
                                      $"but the objective itself carries {objective.MapId}");
                        }
                    }
                }
            }

            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }
    }
}
