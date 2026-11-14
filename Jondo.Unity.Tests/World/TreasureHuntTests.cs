using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The divine dimensions' legendary hunts: the capture «salir de anutropia por portal-navegar
    /// por salas de torre de los viajeros-lanzar busqueda portal», and the rules on a world of
    /// our own -- a square of 21 by 21 outdoor maps, map id 1000 + 100·x + y.
    /// </summary>
    [Collection("MapManager")]
    public class TreasureHuntTests
    {
        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        private static long At(int x, int y) => 1000 + 100 * (x + 10) + (y + 10);

        private static TreasureHunts.Grid Square()
        {
            var maps = new List<(long, int, int)>();
            for (int x = -10; x <= 10; x++)
                for (int y = -10; y <= 10; y++)
                    maps.Add((At(x, y), x, y));
            return new TreasureHunts.Grid(maps);
        }

        private static TreasureHunts.Hunt Hunt(TreasureHunts.Grid grid, int seed = 7)
            => TreasureHunts.Begin(grid, 1, TreasureHunts.Legendary, 61, 0, new Random(seed))!;

        /// <summary>Maps of the square that are none of the hunt's steps nor its start.</summary>
        private static List<long> Elsewhere(TreasureHunts.Grid grid, TreasureHunts.Hunt hunt, int count)
            => grid.Maps.Where(m => m != hunt.StartMap && hunt.Steps.All(s => s.Target != m)).Take(count).ToList();

        /// <summary>Every flag put on its step's map.</summary>
        private static void FlagRight(TreasureHunts.Grid grid, TreasureHunts.Hunt hunt)
        {
            for (int i = hunt.Flags.Count; i < hunt.Steps.Count; i++)
                Assert.Equal(TreasureHunts.FlagOk, TreasureHunts.PutFlag(grid, hunt, i, hunt.Steps[i].Target));
        }

        [Fact]
        public void The_answers_go_as_the_capture_has_them()
        {
            Assert.Equal("08021005", Hex(TreasureHuntProtocol.BuildRequestAnswer(TreasureHunts.Legendary, TreasureHunts.RequestOk)));
            Assert.Equal("08021003", Hex(TreasureHuntProtocol.BuildRequestAnswer(TreasureHunts.Legendary, TreasureHunts.NotAvailable)));
            Assert.Equal("08021002", Hex(TreasureHuntProtocol.BuildRequestAnswer(TreasureHunts.Legendary, TreasureHunts.AlreadyHaveQuest)));
            Assert.Equal("080110b403220131", Hex(ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning,
                TreasureHuntHandler.GiveUpTooSoon, "1")));
            Assert.Equal(TreasureHunts.Legendary, TreasureHuntProtocol.ReadType(Convert.FromHexString("0802")));
            Assert.Equal((2, 3), TreasureHuntProtocol.ReadFlagPut(Pb.New().Var(1, 2).Var(2, 3).Build()));
            Assert.Equal((2, 3), TreasureHuntProtocol.ReadFlagRemoved(Pb.New().Var(1, 3).Var(2, 2).Build()));
        }

        /// <summary>
        /// The hunt as the capture's starts: no stage yet, five stages, four tries, [2,4], five
        /// steps, legendary -- its first step a POI one, ours a «follow the direction» one (hgx f3).
        /// </summary>
        [Fact]
        public void The_hunt_goes_as_the_capture_has_it()
        {
            var hunt = new TreasureHunts.Hunt
            {
                CharacterId = 1,
                Type = TreasureHunts.Legendary,
                StartMap = TreasureHunts.MeasuredStart,
                Steps = Enumerable.Range(0, 5).Select(i => new TreasureHunts.Step
                {
                    Direction = TreasureHunts.South, Maps = 3, Target = 2000 + i,
                }).ToList(),
            };
            string hhf = Hex(TreasureHuntProtocol.BuildHunt(hunt));
            Assert.StartsWith("20053004389386802a4005480252", hhf);

            var steps = ProtoMessage.Parse(TreasureHuntProtocol.BuildHunt(hunt)).Fields.Where(f => f.FieldNumber == 10).ToList();
            var step = ProtoMessage.Parse(Assert.Single(ProtoMessage.Parse(Assert.Single(steps).BytesValue).Fields).BytesValue);
            Assert.Equal(new[] { (2, 3L), (3, 2L) }, step.Fields.Select(f => (f.FieldNumber, f.VarIntValue)));

            hunt.Flags.Add(new TreasureHunts.Flag { MapId = 2000, State = TreasureHunts.FlagWrong });
            var flagged = ProtoMessage.Parse(TreasureHuntProtocol.BuildHunt(hunt));
            Assert.Equal(2, flagged.Fields.Count(f => f.FieldNumber == 10));
            var flag = ProtoMessage.Parse(Assert.Single(flagged.Fields, f => f.FieldNumber == 5).BytesValue);
            Assert.Equal(new[] { (1, 2L), (2, 2000L) }, flag.Fields.Select(f => (f.FieldNumber, f.VarIntValue)));
        }

        /// <summary>
        /// A stage's five steps each so many squares one way, never straight back, never twice on a
        /// map; the next starts where the last was dug.
        /// </summary>
        [Fact]
        public void A_stage_walks_the_squares()
        {
            var grid = Square();
            for (int seed = 0; seed < 30; seed++)
            {
                var hunt = Hunt(grid, seed);
                Assert.Equal(TreasureHunts.StepsPerCheckpoint, hunt.Steps.Count);
                var (x, y) = grid.PositionOf(hunt.StartMap)!.Value;
                var seen = new HashSet<long> { hunt.StartMap };
                int previous = -1;
                foreach (var step in hunt.Steps)
                {
                    Assert.InRange(step.Maps, TreasureHunts.FewestMaps, TreasureHunts.MostMaps);
                    Assert.NotEqual((previous + 4) % 8, step.Direction);
                    var (dx, dy) = step.Direction switch
                    {
                        TreasureHunts.East => (1, 0), TreasureHunts.South => (0, 1), TreasureHunts.West => (-1, 0), _ => (0, -1),
                    };
                    (x, y) = (x + dx * step.Maps, y + dy * step.Maps);
                    Assert.Equal(At(x, y), step.Target);
                    Assert.True(seen.Add(step.Target));
                    previous = step.Direction;
                }
            }
        }

        [Fact]
        public void Flags_go_one_after_another_and_never_twice_on_a_map()
        {
            var grid = Square();
            var hunt = Hunt(grid);
            Assert.Equal(1, hunt.Known);
            Assert.Equal(TreasureHunts.FlagWrongIndex, TreasureHunts.PutFlag(grid, hunt, 1, At(0, 0)));
            Assert.Equal(TreasureHunts.FlagOk, TreasureHunts.PutFlag(grid, hunt, 0, At(0, 0)));
            Assert.Equal(2, hunt.Known);
            Assert.Equal(TreasureHunts.FlagSameMap, TreasureHunts.PutFlag(grid, hunt, 1, At(0, 0)));
            Assert.Equal(TreasureHunts.FlagOk, TreasureHunts.PutFlag(grid, hunt, 1, At(1, 0)));
            Assert.Equal(TreasureHunts.FlagOk, TreasureHunts.PutFlag(grid, hunt, 0, At(2, 0)));
            Assert.Equal(TreasureHunts.FlagTooMany, TreasureHunts.PutFlag(grid, hunt, 5, At(3, 0)));

            // Taken away with those after it; the client's following asks find nothing left.
            Assert.Equal(TreasureHunts.FlagOk, TreasureHunts.RemoveFlag(hunt, 0));
            Assert.Empty(hunt.Flags);
            Assert.Equal(TreasureHunts.FlagOk, TreasureHunts.RemoveFlag(hunt, 1));
        }

        /// <summary>
        /// The dig: every step flagged and standing on the last flag; a wrong flag costs a try,
        /// digging again unchanged says so, the last try loses; all right, the next stage from here.
        /// </summary>
        [Fact]
        public void The_dig_judges_the_stage()
        {
            var grid = Square();
            var hunt = Hunt(grid);
            var dice = new Random(3);
            Assert.Equal(TreasureHunts.DigImpossible, TreasureHunts.Dig(grid, hunt, hunt.StartMap, dice).Result);

            FlagRight(grid, hunt);
            long last = hunt.Steps[^1].Target;
            Assert.Equal(TreasureHunts.DigWrongMap, TreasureHunts.Dig(grid, hunt, hunt.StartMap, dice).Result);

            // One flag elsewhere: three tries left, the flags judged.
            hunt.Flags[1].MapId = Elsewhere(grid, hunt, 1)[0];
            Assert.Equal((TreasureHunts.DigWrong, 1), TreasureHunts.Dig(grid, hunt, last, dice));
            Assert.Equal(3, hunt.Retries);
            Assert.Equal(new[] { 1, 2, 1, 1, 1 }, hunt.Flags.Select(f => f.State));
            Assert.Equal(TreasureHunts.DigNothingNew, TreasureHunts.Dig(grid, hunt, last, dice).Result);
            Assert.Equal(TreasureHunts.FlagImpossible, TreasureHunts.PutFlag(grid, hunt, 0, At(0, 0)));

            Assert.Equal(TreasureHunts.FlagOk, TreasureHunts.PutFlag(grid, hunt, 1, hunt.Steps[1].Target));
            Assert.Equal(TreasureHunts.DigNewStage, TreasureHunts.Dig(grid, hunt, last, dice).Result);
            Assert.Equal(1, hunt.Checkpoint);
            Assert.Equal(last, hunt.StartMap);
            Assert.Empty(hunt.Flags);
            Assert.Equal(3, hunt.Retries);
        }

        [Fact]
        public void The_last_try_loses_the_hunt()
        {
            var grid = Square();
            var hunt = Hunt(grid);
            hunt.Retries = 1;
            var elsewhere = Elsewhere(grid, hunt, hunt.Steps.Count);
            for (int i = 0; i < hunt.Steps.Count; i++)
                Assert.Equal(TreasureHunts.FlagOk, TreasureHunts.PutFlag(grid, hunt, i,
                    i == hunt.Steps.Count - 1 ? hunt.Steps[i].Target : elsewhere[i]));
            Assert.Equal(TreasureHunts.DigLost, TreasureHunts.Dig(grid, hunt, hunt.Steps[^1].Target, new Random(1)).Result);
        }

        /// <summary>The fifth stage's dig is the fight, and stays so until the fissure falls.</summary>
        [Fact]
        public void The_last_stage_ends_in_the_fight()
        {
            var grid = Square();
            var hunt = Hunt(grid);
            var dice = new Random(5);
            for (int stage = 0; stage < TreasureHunts.Checkpoints - 1; stage++)
            {
                FlagRight(grid, hunt);
                Assert.Equal(TreasureHunts.DigNewStage, TreasureHunts.Dig(grid, hunt, hunt.Steps[^1].Target, dice).Result);
            }
            Assert.True(hunt.LastCheckpoint);
            FlagRight(grid, hunt);
            Assert.False(hunt.ReadyToFight);
            Assert.Equal(TreasureHunts.DigFinished, TreasureHunts.Dig(grid, hunt, hunt.Steps[^1].Target, dice).Result);
            Assert.True(hunt.ReadyToFight);
            Assert.Equal(TreasureHunts.DigFinished, TreasureHunts.Dig(grid, hunt, hunt.Steps[^1].Target, dice).Result);
        }

        /// <summary>The capture's give-ups, 9 and 18 seconds in: «espera 1 minuto/s más».</summary>
        [Fact]
        public void A_hunt_is_given_up_after_its_first_minute()
        {
            var hunt = new TreasureHunts.Hunt { StartedMs = 1_000_000 };
            Assert.Equal(1, TreasureHunts.MinutesBeforeGivingUp(hunt, 1_009_000));
            Assert.Equal(1, TreasureHunts.MinutesBeforeGivingUp(hunt, 1_018_000));
            Assert.Equal(0, TreasureHunts.MinutesBeforeGivingUp(hunt, 1_060_000));
        }

        /// <summary>
        /// The models' answers: 3 for the capture's Zurcalia, whose portal the character had, and for
        /// a level under 50; 2 with a hunt under way, as the capture's Osavora.
        /// </summary>
        [Fact]
        public void The_model_answers_as_the_capture()
        {
            var zurcalia = TravelersTower.ModelOf(502767);
            var osavora = TravelersTower.ModelOf(539218);
            bool Captured(long c, int a) => a is 763 or 764 or 766;
            TreasureHunts.Hunt? None(long c, int t) => null;
            Assert.Equal(TreasureHunts.NotAvailable, TreasureHuntHandler.Answer(zurcalia, 1, 200, Captured, None));
            Assert.Equal(TreasureHunts.NotAvailable, TreasureHuntHandler.Answer(osavora, 1, 49, Captured, None));
            Assert.Equal(TreasureHunts.AlreadyHaveQuest, TreasureHuntHandler.Answer(osavora, 1, 200, Captured,
                (c, t) => new TreasureHunts.Hunt { Type = t }));
            int ok = TreasureHuntHandler.Answer(osavora, 1, 200, Captured, None);
            Assert.Equal(LegendaryHunts.Of(62) == null ? TreasureHunts.NoQuestFound : TreasureHunts.RequestOk, ok);
        }

        /// <summary>The fissure first, then its dimension's monsters, each at the grade nearest the hunt's level.</summary>
        [Fact]
        public void The_fissure_comes_with_its_dimensions_monsters()
        {
            var legendary = new LegendaryHunts.Definition(61, 0, 120, 8175, 32511, 31783, 10);
            MobSpawnManager.MonsterData Monster(int id, params int[] levels) => new()
            {
                Id = id,
                Grades = levels.Select(l => new MobSpawnManager.MonsterGrade { Level = l }).ToList(),
            };
            var data = new Dictionary<int, MobSpawnManager.MonsterData>
            {
                [8175] = Monster(8175, 120, 120, 120, 120, 120),
                [1] = Monster(1, 100, 110, 121, 130, 140),
            };
            var fight = TreasureHuntHandler.FissureFight(legendary, new[] { 1, 1, 2 }, id => data.GetValueOrDefault(id), new Random(1));
            Assert.Equal((8175, 0), fight[0]);
            Assert.Equal(1 + TreasureHuntHandler.FissureCompany, fight.Count);
            Assert.All(fight.Skip(1), m => Assert.Equal((1, 2), m));
        }

        /// <summary>
        /// A criminal hunt has no dimension to draw company from, but «Trésor des bandits de Cania»
        /// ends against the three bandits: the two its row leaves out come from the authored company.
        /// </summary>
        [Fact]
        public void The_bandits_of_cania_wait_together()
        {
            Assert.Equal(new[] { 6543, 6544 }, LegendaryHunts.CompanyOf(57));
            Assert.Empty(LegendaryHunts.CompanyOf(8));

            var legendary = new LegendaryHunts.Definition(57, 0, 100, 6542, 23744, 23743, 10);
            MobSpawnManager.MonsterData Monster(int id) => new()
            {
                Id = id,
                Grades = new[] { 100, 100, 100, 100, 100 }.Select(l => new MobSpawnManager.MonsterGrade { Level = l }).ToList(),
            };
            var data = new[] { 6542, 6543, 6544 }.ToDictionary(id => id, Monster);
            var fight = TreasureHuntHandler.FissureFight(legendary, Array.Empty<int>(), id => data.GetValueOrDefault(id),
                                                         new Random(1), LegendaryHunts.CompanyOf(57));
            Assert.Equal(new[] { 6542, 6543, 6544 }, fight.Select(m => m.Monster));
        }

        [Fact]
        public void The_steps_and_flags_are_kept_as_written()
        {
            var steps = new List<TreasureHunts.Step> { new() { Direction = 6, Maps = 4, Target = 88081171 } };
            var kept = TreasureHunts.ReadSteps(TreasureHunts.WriteSteps(steps));
            Assert.Equal((6, 4, 88081171L), (kept[0].Direction, kept[0].Maps, kept[0].Target));
            var flags = TreasureHunts.ReadFlags(TreasureHunts.WriteFlags(new[] { new TreasureHunts.Flag { MapId = 5, State = 2 } }));
            Assert.Equal((5L, 2), (flags[0].MapId, flags[0].State));
            Assert.Empty(TreasureHunts.ReadFlags(""));
        }

        /// <summary>
        /// The localization room's five models, declared as the capture's jss has them -- type
        /// named after the dimension, «Localizar una fisura» --, and none of them a 2.68 passage.
        /// </summary>
        [Fact]
        public void The_models_launch_the_hunts()
        {
            if (!MapManager.WalkableCells.ContainsKey(TravelersTower.FissureRoom)) MapManager.Initialize();
            if (MapManager.GetMapInfo(TravelersTower.FissureRoom) == null) return;   // no world data
            Interactives.Initialize();
            if (Interactives.ElementsOf(TravelersTower.FissureRoom).Count == 0) return;
            TeleportManager.Initialize();
            InteractiveRegistry.Initialize();

            foreach (var dimension in TravelersTower.Dimensions)
            {
                Assert.False(TeleportManager.TryGet(TravelersTower.FissureRoom, dimension.Model, out _));
                var model = Assert.Single(InteractiveRegistry.OnMap(TravelersTower.FissureRoom), i => i.Element.Id == dimension.Model);
                Assert.Equal(dimension.ModelType, model.Type);
                var action = Assert.Single(model.Actions);
                Assert.Equal(InteractiveActionKind.FissureModel, action.Kind);
                Assert.Equal((184, 360), (action.SkillId, action.SkillNameId));
                var legendary = LegendaryHunts.Of(dimension.Legendary);
                if (legendary != null) Assert.Equal(dimension.Name, LegendaryHunts.EndingWith(legendary.Monster) is { } l
                    ? TravelersTower.LegendaryOf(l.Id).Name : null);
            }
        }

        /// <summary>
        /// The capture's hunt byte for byte: its first step «882 to the south», the «Champiñón
        /// rayado» -- a landmark step, f10 { f1 { f1 882, f2 2 } }.
        /// </summary>
        [Fact]
        public void A_landmark_step_goes_as_the_capture_has_it()
        {
            var hunt = new TreasureHunts.Hunt
            {
                CharacterId = 1,
                Type = TreasureHunts.Legendary,
                StartMap = TreasureHunts.MeasuredStart,
                Steps = Enumerable.Range(0, 5).Select(i => new TreasureHunts.Step
                {
                    Direction = TreasureHunts.South, Maps = 10, Target = 88081161 + i, Poi = i == 0 ? 882 : 0,
                }).ToList(),
            };
            Assert.Equal("20053004389386802a4005480252070a0508f2061002", Hex(TreasureHuntProtocol.BuildHunt(hunt)));

            var kept = TreasureHunts.ReadSteps(TreasureHunts.WriteSteps(hunt.Steps));
            Assert.Equal(882, kept[0].Poi);
            Assert.Equal(0, kept[1].Poi);
            Assert.Equal(hunt.Steps[0].Target, kept[0].Target);
        }

        /// <summary>
        /// Where the maps show landmarks, a step is the first map that way showing one no map before
        /// it shows, within ten.
        /// </summary>
        [Fact]
        public void A_landmark_step_is_the_first_map_that_way_to_show_it()
        {
            var grid = Square();
            IReadOnlyList<int> PoisOf(long map)
            {
                var (x, y) = grid.PositionOf(map)!.Value;
                return new[] { 900 + ((x * 7 + y * 13) % 5 + 5) % 5 };
            }

            int landmarks = 0;
            for (int seed = 0; seed < 30; seed++)
            {
                var steps = TreasureHunts.Route(grid, At(0, 0), new Random(seed), PoisOf);
                Assert.NotNull(steps);
                var (x, y) = (0, 0);
                foreach (var step in steps!)
                {
                    var (dx, dy) = step.Direction switch
                    {
                        TreasureHunts.East => (1, 0), TreasureHunts.South => (0, 1), TreasureHunts.West => (-1, 0), _ => (0, -1),
                    };
                    Assert.Equal(At(x + dx * step.Maps, y + dy * step.Maps), step.Target);
                    if (step.Poi > 0)
                    {
                        landmarks++;
                        Assert.Contains(step.Poi, PoisOf(step.Target));
                        for (int between = 1; between < step.Maps; between++)
                            Assert.DoesNotContain(step.Poi, PoisOf(At(x + dx * between, y + dy * between)));
                    }
                    (x, y) = (x + dx * step.Maps, y + dy * step.Maps);
                }
            }
            Assert.Equal(30 * TreasureHunts.StepsPerCheckpoint, landmarks);
        }

        /// <summary>The landmarks as the players have mapped them: the capture's 882, first south of [2,4] on [2,11].</summary>
        [Fact]
        public void The_landmarks_are_where_the_players_mapped_them()
        {
            if (TreasureHuntPois.Count == 0) return;   // no data
            Assert.Contains(882, TreasureHuntPois.Of(88081164));
            for (long between = 88081170; between > 88081164; between--)
                Assert.DoesNotContain(882, TreasureHuntPois.Of(between));
            Assert.Empty(TreasureHuntPois.Of(1));
        }
    }
}
