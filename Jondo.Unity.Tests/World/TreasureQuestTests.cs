using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Malle aux trésors' hunts: Dhain's quests, the Anutrof box, the Treasure fought at the end
    /// and the chest that ticks the quest (DofusPourLesNoobs, Les chasses aux trésors; the client's
    /// quests 2538-2543 and items 32379-32386).
    /// </summary>
    [Collection("MapManager")]
    public class TreasureQuestTests
    {
        private static Fighter Monster(long id, int monster, int life = 1)
            => new Fighter { Id = id, MonsterId = monster, TeamId = FightInstance.Rojos, CurrentHP = life, MaxHP = life };

        private static (FightInstance Fight, Fighter Treasure, List<Fighter> Chests, Fighter Player) Fight()
        {
            var fight = new FightInstance(1, 1);
            var player = new Fighter { Id = 10, TeamId = FightInstance.Azules, CurrentHP = 1000, MaxHP = 1000 };
            fight.AddPlayer(player);
            var treasure = Monster(-1, TreasureFight.Treasure, 7);
            fight.AddMonster(treasure);
            var chests = TreasureFight.Chests.Select((c, i) => Monster(-2 - i, c)).ToList();
            foreach (var chest in chests) fight.AddMonster(chest);
            return (fight, treasure, chests, player);
        }

        /// <summary>«Frapper ce dernier … retire 1 PV au Trésor»: the chest on legs is right, and another is drawn.</summary>
        [Fact]
        public void The_chest_on_legs_takes_a_life()
        {
            var (fight, treasure, chests, player) = Fight();
            var dice = new Random(3);
            Assert.Equal(TreasureFight.Outcome.Nothing, TreasureFight.Judge(fight, player, treasure, dice));
            for (int blow = 0; blow < 10; blow++)
            {
                // The first blow draws the chest on legs; after that, it is the one to hit.
                if (TreasureFight.OnLegs(fight) == 0) TreasureFight.Judge(fight, player, chests[0], dice);
                var onLegs = chests.Single(c => c.Id == TreasureFight.OnLegs(fight));
                Assert.Equal(TreasureFight.Outcome.Right, TreasureFight.Judge(fight, player, onLegs, dice));
            }
            Assert.Same(treasure, TreasureFight.TreasureOf(fight, chests[0]));
        }

        /// <summary>«Au troisième échec, le joueur est OS».</summary>
        [Fact]
        public void The_third_wrong_chest_kills()
        {
            var (fight, _, chests, player) = Fight();
            var dice = new Random(5);
            var outcomes = new List<TreasureFight.Outcome>();
            while (outcomes.Count < 3)
            {
                TreasureFight.Outcome outcome;
                long onLegs = TreasureFight.OnLegs(fight);
                var wrong = chests.First(c => c.Id != onLegs);
                outcome = TreasureFight.Judge(fight, player, wrong, dice);
                if (outcome != TreasureFight.Outcome.Right) outcomes.Add(outcome);
            }
            Assert.Equal(new[] { TreasureFight.Outcome.Wrong, TreasureFight.Outcome.Wrong, TreasureFight.Outcome.Fatal }, outcomes);
        }

        [Fact]
        public void The_treasure_summons_each_chest_once()
        {
            var (fight, treasure, chests, player) = Fight();
            Assert.True(TreasureFight.AlreadyOut(fight, treasure, chests[0].MonsterId));
            chests[0].CurrentHP = 0;
            Assert.False(TreasureFight.AlreadyOut(fight, treasure, chests[0].MonsterId));
            Assert.False(TreasureFight.AlreadyOut(fight, player, chests[1].MonsterId));
            Assert.True(TreasureFight.Guards(treasure));
            Assert.True(TreasureFight.Guards(chests[3]));
            Assert.False(TreasureFight.Guards(player));
        }

        [Fact]
        public void Each_quest_has_its_name_and_chest()
        {
            Assert.Equal(32382, TreasureQuests.OfQuest(2539)!.Chest);
            Assert.Equal(2539, TreasureQuests.OfName(362)!.Quest);
            Assert.Equal(2543, TreasureQuests.OfChest(32384)!.Quest);
            Assert.Equal(new[] { 362, 363, 364, 365, 366, 367 }, TreasureQuests.Kinds.Select(k => k.SkillName).OrderBy(n => n));
        }

        /// <summary>The Treasure's grades go from 20 to 200 by twenty: the highest one reached.</summary>
        [Fact]
        public void The_treasure_matches_the_hunter()
        {
            var levels = Enumerable.Range(1, 10).Select(i => i * 20).ToList();
            Assert.Equal(0, TreasureQuests.TreasureGrade(levels, 20));
            Assert.Equal(0, TreasureQuests.TreasureGrade(levels, 39));
            Assert.Equal(4, TreasureQuests.TreasureGrade(levels, 115));
            Assert.Equal(9, TreasureQuests.TreasureGrade(levels, 200));
            Assert.True(TreasureQuests.StartsFor(20, 1));
            Assert.True(TreasureQuests.StartsFor(110, 120));
            Assert.False(TreasureQuests.StartsFor(150, 120));
        }

        /// <summary>«une fois par jour à partir de 7h»: a hunting day runs from 7:00 to 7:00 in Paris.</summary>
        [Fact]
        public void A_hunting_day_starts_at_seven()
        {
            var summer = TimeSpan.FromHours(2);
            Assert.Equal("2026-10-20", TreasureQuests.DayOf(new DateTimeOffset(2026, 10, 21, 6, 59, 0, summer)));
            Assert.Equal("2026-10-21", TreasureQuests.DayOf(new DateTimeOffset(2026, 10, 21, 7, 0, 0, summer)));
            Assert.Equal("2026-12-01", TreasureQuests.DayOf(new DateTimeOffset(2026, 12, 1, 6, 0, 0, TimeSpan.Zero)));
        }

        /// <summary>A classic hunt: its own stages and steps, its quest, and the stages in the hunt's f4.</summary>
        [Fact]
        public void A_classic_hunt_has_its_stages()
        {
            var maps = new List<(long, int, int)>();
            for (int x = -10; x <= 10; x++)
                for (int y = -10; y <= 10; y++)
                    maps.Add((1000 + 100 * (x + 10) + (y + 10), x, y));
            var grid = new TreasureHunts.Grid(maps);
            var hunt = TreasureHunts.Begin(grid, 1, TreasureHunts.Classic, 0, 0, new Random(2),
                                           stages: 3, stepsPerStage: TreasureQuests.StepsPerStage, quest: 2539,
                                           startsOn: map => map % 2 == 0)!;
            Assert.Equal((3, 5, 2539), (hunt.Stages, hunt.Steps.Count, hunt.Quest));
            Assert.Equal(0, hunt.StartMap % 2);
            var hhf = ProtoMessage.Parse(TreasureHuntProtocol.BuildHunt(hunt));
            Assert.Equal(3, hhf.Fields.Single(f => f.FieldNumber == 4).VarIntValue);
            Assert.DoesNotContain(hhf.Fields, f => f.FieldNumber == 9);   // type 0, the classic one, goes unsaid

            var dice = new Random(4);
            for (int stage = 0; stage < 2; stage++)
            {
                for (int i = 0; i < hunt.Steps.Count; i++) TreasureHunts.PutFlag(grid, hunt, i, hunt.Steps[i].Target);
                Assert.Equal(TreasureHunts.DigNewStage, TreasureHunts.Dig(grid, hunt, hunt.Steps[^1].Target, dice).Result);
                Assert.Equal(5, hunt.Steps.Count);
            }
            Assert.True(hunt.LastCheckpoint);
        }

        /// <summary>The «Cofre del día» validates «Encuentra y abre el cofre del día» with its effect 30.</summary>
        [Fact]
        public void The_chest_validates_its_objective()
        {
            var objectives = ItemUseHandler.QuestObjectivesOf(32382);
            if (objectives.Count == 0) return;   // no world data
            Assert.Equal(new[] { 19525 }, objectives);
        }

        /// <summary>The box declared with the six names, under skill 184.</summary>
        [Fact]
        public void The_box_offers_the_hunts()
        {
            Interactives.Initialize();
            if (Interactives.ElementsOf(TreasureQuests.Room).Count == 0) return;   // no world data
            InteractiveRegistry.Initialize();
            var box = Assert.Single(InteractiveRegistry.OnMap(TreasureQuests.Room), i => i.Element.Id == TreasureQuests.Box);
            Assert.Equal(TreasureQuests.BoxType, box.Type);
            Assert.All(box.Actions, a => Assert.Equal((InteractiveActionKind.TreasureBox, 184), (a.Kind, a.SkillId)));
            Assert.Equal(new[] { 362, 363, 364, 365, 366, 367 }, box.Actions.Select(a => a.SkillNameId));
        }

        /// <summary>The Pupitre makes maps (275), the planning table opens the legendary hunts (277).</summary>
        [Fact]
        public void The_room_has_its_desk_and_planning_table()
        {
            Interactives.Initialize();
            if (Interactives.ElementsOf(TreasureQuests.Room).Count == 0) return;   // no world data
            InteractiveRegistry.Initialize();
            var room = InteractiveRegistry.OnMap(TreasureQuests.Room).ToList();
            var desk = Assert.Single(room, i => i.Element.Id == TreasureQuests.Desk);
            Assert.Equal((InteractiveActionKind.Workshop, 275), (desk.Actions.Single().Kind, desk.Actions.Single().SkillId));
            var table = Assert.Single(room, i => i.Element.Id == TreasureQuests.PlanningTable);
            Assert.Equal((InteractiveActionKind.PlanningTable, 277), (table.Actions.Single().Kind, table.Actions.Single().SkillId));
        }

        /// <summary>The planning table offers the wanted criminals' hunts -- Gein's is 5 --, not the fissures'.</summary>
        [Fact]
        public void The_planning_table_offers_the_wanted_hunts()
        {
            var offered = TreasureHuntHandler.PlanningTableHunts();
            if (offered.Count == 0) return;   // no client data
            Assert.Contains(5, offered);
            Assert.All(TravelersTower.Dimensions, d => Assert.DoesNotContain(d.Legendary, offered));
        }

        /// <summary>One legendary hunt at a time, and its map in the bag.</summary>
        [Fact]
        public void A_legendary_hunt_needs_its_map()
        {
            var gein = LegendaryHunts.Of(5);
            if (gein == null) return;   // no client data
            Assert.Equal(TreasureHunts.RequestOk, TreasureHuntHandler.LegendaryAnswer(gein, false, 1));
            Assert.Equal(TreasureHunts.NotAvailable, TreasureHuntHandler.LegendaryAnswer(gein, false, 0));
            Assert.Equal(TreasureHunts.AlreadyHaveQuest, TreasureHuntHandler.LegendaryAnswer(gein, true, 1));
            Assert.Equal(TreasureHunts.NoQuestFound, TreasureHuntHandler.LegendaryAnswer(null, false, 1));
            var fissure = LegendaryHunts.Of(TravelersTower.Dimensions[0].Legendary);
            if (fissure != null) Assert.Equal(TreasureHunts.NoQuestFound, TreasureHuntHandler.LegendaryAnswer(fissure, false, 1));
        }

        /// <summary>
        /// The tablet of Totankama is studied only with «El tesoro de Totankama» under way, as its
        /// description says; a hunt whose map says nothing of a quest asks for none.
        /// </summary>
        [Fact]
        public void The_tablet_of_totankama_needs_its_quest()
        {
            var totankama = LegendaryHunts.All.FirstOrDefault(h => h.MapItem == 15391);
            if (totankama == null) return;   // no client data
            Assert.Equal(1523, LegendaryHunts.QuestOf(totankama));
            Assert.Equal(new[] { 8669, 8670 }, LegendaryHunts.ObjectivesBefore(totankama));
            Assert.Equal(TreasureHunts.NotAvailable, TreasureHuntHandler.LegendaryAnswer(totankama, false, 1, questUnderWay: false));
            Assert.Equal(TreasureHunts.RequestOk, TreasureHuntHandler.LegendaryAnswer(totankama, false, 1, questUnderWay: true));
            var gein = LegendaryHunts.Of(5);
            if (gein != null) Assert.Equal(0, LegendaryHunts.QuestOf(gein));
        }

        /// <summary>The planning table's message: the ids, packed in f1.</summary>
        [Fact]
        public void The_planning_tables_hunts_travel_packed()
            => Assert.Equal("0a03050810", Convert.ToHexString(TreasureHuntProtocol.BuildLegendaryHunts(new[] { 5, 8, 16 })).ToLowerInvariant());
    }
}
