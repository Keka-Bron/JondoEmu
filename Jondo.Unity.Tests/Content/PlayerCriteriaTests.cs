using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The monsters' quest drops: their criteria judged strictly for whoever would receive them.
    /// </summary>
    public class PlayerCriteriaTests
    {
        private static bool Met(string criterion, PlayerCriteria.Facts facts)
            => Criterion.Met(criterion, condition => PlayerCriteria.Resolve(condition, facts));

        [Fact]
        public void BI_compares_the_zero_accessible_state_for_player_drop_criteria()
        {
            var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => 1);
            Assert.False(Met("BI=1", facts));
            Assert.True(Met("BI!1", facts));
            Assert.True(Met("BI=0", facts));
            Assert.False(Met("BI!0", facts));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("BI=1,2", condition => PlayerCriteria.Resolve(condition, facts)));
        }

        [Fact]
        public void PX_checks_authoritative_administrator_role_for_selector_A()
        {
            var administrator = new PlayerCriteria.Facts(null, null, null, 0, _ => 0, IsAdministrator: () => true);
            Assert.True(Met("PX=A", administrator));
            Assert.False(Met("PX!A", administrator));

            var player = administrator with { IsAdministrator = () => false };
            Assert.False(Met("PX=A", player));
            Assert.True(Met("PX!A", player));
        }

        [Fact]
        public void PX_keeps_unavailable_or_unsupported_role_checks_unknown()
        {
            var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => 0, IsAdministrator: () => null);
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("PX=A", condition => PlayerCriteria.Resolve(condition, facts)));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("PX!A", condition => PlayerCriteria.Resolve(condition, facts)));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("PX=B", condition => PlayerCriteria.Resolve(condition, facts with { IsAdministrator = () => true })));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("PX>A", condition => PlayerCriteria.Resolve(condition, facts with { IsAdministrator = () => true })));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("PX=A,extra", condition => PlayerCriteria.Resolve(condition, facts with { IsAdministrator = () => true })));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("PX=A", condition => PlayerCriteria.Resolve(condition, facts with { IsAdministrator = null })));
        }

        [Fact]
        public void Ot_checks_character_title_ownership_and_keeps_unavailable_state_unknown()
        {
            var owner = new PlayerCriteria.Facts(null, null, null, 0, _ => 0, KnownTitle: id => id == 560);
            Assert.True(Met("Ot=560", owner));
            Assert.False(Met("Ot!560", owner));
            Assert.False(Met("Ot=560", owner with { KnownTitle = _ => false }));
            Assert.True(Met("Ot!560", owner with { KnownTitle = _ => false }));

            var unavailable = owner with { KnownTitle = _ => null };
            Assert.False(Met("Ot=560", unavailable));
            Assert.False(Met("Ot!560", unavailable));
            Assert.False(Met("Ot", owner));
        }

        /// <summary>«PO!x» none, «PO=x» one at least, «PO&lt;x,n» fewer than n.</summary>
        [Fact]
        public void What_is_held_is_counted()
        {
            int held = 0;
            var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => held);
            Assert.True(Met("PO!11683", facts));
            Assert.False(Met("PO=11683", facts));
            Assert.True(Met("PO<2269,10", facts));
            held = 10;
            Assert.False(Met("PO!11683", facts));
            Assert.True(Met("PO=11683", facts));
            Assert.False(Met("PO<2269,10", facts));
            Assert.True(Met("PO>2269,9", facts));
        }

        [Fact]
        public void Pn_checks_equipment_slots_and_the_whole_equipment_empty_sentinel()
        {
            bool empty = true;
            int slotCalls = 0;
            var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => 0,
                OccupiedEquipmentSlot: slot =>
                {
                    Assert.InRange(slot, 0, Equipment.LastWornSlot);
                    slotCalls++;
                    return slot == 0;
                },
                EmptyEquipment: () => empty);

            // The corpus uses Pn!slot for an empty slot and -1 for empty equipment as a whole.
            Assert.True(Met("Pn!1", facts));
            Assert.False(Met("Pn=1", facts));
            Assert.False(Met("Pn!0", facts));
            Assert.True(Met("Pn=0", facts));
            Assert.True(Met("Pn!-1", facts));
            Assert.False(Met("Pn=-1", facts));
            Assert.Equal(4, slotCalls); // -1 was never passed to the slot lookup.

            empty = false;
            Assert.False(Met("Pn!-1", facts));
            Assert.True(Met("Pn=-1", facts));
            Assert.True(Met("Pn!1", facts));
        }

        [Fact]
        public void Pn_keeps_malformed_unsupported_and_unavailable_equipment_unknown()
        {
            var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => 0,
                OccupiedEquipmentSlot: _ => false,
                EmptyEquipment: () => null);

            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Pn!1,2", condition => PlayerCriteria.Resolve(condition, facts)));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Pn!-2", condition => PlayerCriteria.Resolve(condition, facts)));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Pn>1", condition => PlayerCriteria.Resolve(condition, facts)));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Pn!-1", condition => PlayerCriteria.Resolve(condition, facts)));

            var noSlotLookup = facts with { OccupiedEquipmentSlot = null };
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Pn!1", condition => PlayerCriteria.Resolve(condition, noSlotLookup)));
        }

        [Fact]
        public void Ss_checks_live_monster_presence_in_the_requested_subarea()
        {
            var calls = new List<(int Monster, int Subarea)>();
            var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => 0,
                LiveMonsterInSubarea: (monster, subarea) =>
                {
                    calls.Add((monster, subarea));
                    return monster == 622 && subarea == 2970;
                });

            Assert.True(Met("Ss=622,2970", facts));
            Assert.False(Met("Ss!622,2970", facts));
            Assert.Equal(new[] { (622, 2970), (622, 2970) }, calls);

            var absent = facts with { LiveMonsterInSubarea = (_, _) => false };
            Assert.True(Met("Ss!622,2970", absent));
            Assert.False(Met("Ss=622,2970", absent));
        }

        [Fact]
        public void Ss_keeps_unavailable_or_malformed_live_presence_unknown()
        {
            var unavailable = new PlayerCriteria.Facts(null, null, null, 0, _ => 0);
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Ss!622,2970", c => PlayerCriteria.Resolve(c, unavailable)));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Ss!622,2970", c => PlayerCriteria.Resolve(c,
                unavailable with { LiveMonsterInSubarea = (_, _) => null })));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Ss!622", c => PlayerCriteria.Resolve(c,
                unavailable with { LiveMonsterInSubarea = (_, _) => true })));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Ss!0,2970", c => PlayerCriteria.Resolve(c,
                unavailable with { LiveMonsterInSubarea = (_, _) => true })));
        }

        /// <summary>The hunter's meat: job 41 above 9, a hunting weapon in hand.</summary>
        [Fact]
        public void Meat_falls_to_hunters_with_their_weapon()
        {
            int level = 10;
            bool weapon = true;
            var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => 0, job => job == 41 ? level : 0, () => weapon);
            Assert.True(Met("Pj>41,9&CU>0", facts));
            Assert.False(Met("Pj>41,19&CU>0", facts));
            weapon = false;
            Assert.False(Met("Pj>41,9&CU>0", facts));
            Assert.True(Met("PJ=41", facts));
            Assert.False(Met("PJ=28", facts));
        }

        [Fact]
        public void Mt_is_whether_the_monster_that_drops_is_a_boss()
        {
            // The boss flag of the monster's template: the Lord Crow (289) has it, a tofu (98) not.
            if (!Jondo.Unity.Server.Managers.Dreams.IsBoss(289)) return;   // no monster data here

            var boss = new PlayerCriteria.Facts(null, null, null, 0, _ => 0, MonsterId: 289);
            Assert.True(Met("Mt=1", boss));
            Assert.False(Met("Mt=0", boss));

            var tofu = boss with { MonsterId = 98 };
            Assert.True(Met("Mt=0", tofu));
            Assert.False(Met("Mt=1", tofu));

            // Without a monster there is nothing to say.
            Assert.Equal(Answer.Unknown, PlayerCriteria.Resolve(new Condition("Mt", '=', ["1"]), boss with { MonsterId = 0 }));
        }

        /// <summary>
        /// The young's equipment off the piwis and the like, «(SC!5|ST!5)&amp;PL&gt;0&amp;PL&lt;59»: on a classic
        /// server, not the fifth Temporis, for whoever is below level 59. The server's letters are the
        /// character's in session; without one they stay unknown.
        /// </summary>
        [Fact]
        public void The_youngs_equipment_drops_on_a_classic_server()
        {
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                session.State.CharacterLevel = 20;
                var facts = new PlayerCriteria.Facts(session.State, null, null, 0, _ => 0);
                Assert.True(Met("(SC!5|ST!5)&PL>0&PL<59", facts));
                Assert.False(Met("SC=5", facts));
                Assert.False(Met("ST=5", facts));

                // The date, Paris's: «SG=08&Sd>18&Sd<27» is the end of August.
                var today = OsavoraSeasons.InParis(System.DateTimeOffset.UtcNow);
                Assert.True(Met($"SG={today.Month:00}&Sd={today.Day}", facts));
                Assert.False(Met($"SG!{today.Month}", facts));

                // Still strict: a letter nobody answers keeps the row from dropping.
                Assert.False(Met("SC!5&Sv!946,0", facts));

                // The piwis' pollen wants a pollen net received less than thirty days ago: without
                // one there is no date to count from, and nothing falls.
                const string pollen = "PL<21&DD<30,21172";
                Assert.False(Met(pollen, facts));
                var netHolder = facts with { Held = item => item == 21172 ? 1 : 0 };
                Assert.True(Met(pollen, netHolder));


                session.State.CharacterLevel = 60;
                Assert.False(Met("(SC!5|ST!5)&PL>0&PL<59", facts));
            }

            var nobody = new PlayerCriteria.Facts(null, null, null, 0, _ => 0);
            Assert.False(Met("SC!5", nobody));
        }

        [Fact]
        public void Sc_with_an_unknown_id_is_unknown_either_way()
        {
            var noCharacter = new PlayerCriteria.Facts(null, null, null, 0, _ => 0);

            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Sc=987654321", condition => PlayerCriteria.Resolve(condition, noCharacter)));
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Sc!987654321", condition => PlayerCriteria.Resolve(condition, noCharacter)));
        }

        /// <summary>The server's own letters are not known here, and an unknown row never drops.</summary>
        [Fact]
        public void What_is_not_known_does_not_drop()
        {
            var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => 0);
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("Sv!946,0", condition => PlayerCriteria.Resolve(condition, facts)));
            var achievementFacts = new Achievements.CharacterFacts();
            Assert.Null(achievementFacts.Scalar("Sv"));
            Assert.Null(achievementFacts.Count("Sv", 946, "0"));
            Assert.False(Met("Ms=0&Mt=1&Sc=947", facts));
            Assert.False(Met("Pj>41,9&CU>0", facts));   // nobody's jobs to ask
            Assert.False(Met("PL>9", facts));   // no character to ask
            Assert.True(Met("Pm=98567687|PO!1", facts));
        }

        [Fact]
        public void Qc_checks_quest_availability_separately_from_completion()
        {
            if (!File.Exists(Paths.QuestsJson)) return;

            var book = new QuestCatalogue();
            if (!book.Ready || book.Of(2432) == null || book.Of(56) == null) return;

            int level = 50;
            var log = new QuestLog(book, () => level, () => 0);
            var facts = new PlayerCriteria.Facts(null, log, book, 0, _ => 0);

            // 2432 has the fully known prerequisite PL>89: too low means unavailable.
            Assert.False(Met("Qc=2432", facts));
            Assert.True(Met("Qc!2432", facts));
            Assert.False(Met("Qf=2432", facts)); // availability is not completion.

            level = 90;
            Assert.True(Met("Qc=2432", facts));
            Assert.False(Met("Qc!2432", facts));
            Assert.False(Met("Qf=2432", facts));

            // An active quest cannot be taken again, although it is not complete.
            log.Restore(2432, book.Of(2432)!.Steps[0].Id, null, finished: false);
            Assert.False(Met("Qc=2432", facts));
            Assert.True(Met("Qc!2432", facts));
            Assert.False(Met("Qf=2432", facts));

            // Quest 56 also depends on alignment facts this engine does not have. Do not guess
            // availability from CanStart's fail-open handling of those skipped prerequisites.
            var uncertain = new QuestLog(book, () => 90, () => 0);
            var uncertainFacts = facts with { Log = uncertain };
            Assert.False(Met("Qc=56", uncertainFacts));
            Assert.False(Met("Qc!56", uncertainFacts));

            var missingCatalogue = facts with { Book = null };
            Assert.False(Met("Qc=2432", missingCatalogue));
            Assert.False(Met("Qc!2432", missingCatalogue));
        }

        /// <summary>
        /// Quest 632's step 1237: «Qs=1237&amp;Qo&gt;4096&amp;Qo&lt;4098&amp;PO!11683», the step under way with
        /// objective 4097 still to do and the item not yet held.
        /// </summary>
        [Fact]
        public void A_quest_item_drops_while_its_objective_is_pending()
        {
            if (!File.Exists(Paths.QuestsJson)) return;
            var book = new QuestCatalogue();
            if (book.Step(1237) == null) return;
            var log = new QuestLog(book, () => 200, () => 0);
            var run = log.Restore(632, 1237, null, false);

            int held = 0;
            var facts = new PlayerCriteria.Facts(null, log, book, 0, _ => held);
            const string item = "Qs=1237&Qo>4096&Qo<4098&PO!11683";
            // «Qo>4096» is 4096 done and «Qo<4098» is 4098 not done: before 4096 nothing drops.
            Assert.False(Met(item, facts));
            foreach (var objective in book.Step(1237)!.Objectives.Where(o => o.Id <= 4096)) run.Done.Add(objective.Id);
            Assert.True(Met(item, facts));
            Assert.True(Met("Qa=632&PO<2269,10", facts));
            Assert.False(Met("Qs=1238", facts));
            Assert.False(Met("Qa=633", facts));

            held = 1;
            Assert.False(Met(item, facts));

            // Every objective of the step done: nothing pending, nothing drops.
            held = 0;
            foreach (var objective in book.Step(1237)!.Objectives) run.Done.Add(objective.Id);
            Assert.False(Met(item, facts));
        }

        /// <summary>
        /// Frigost's vaccine, «Los monólogos de la vacuna» (615): its first step wants six jalamut
        /// blood samples for the doctor, and the jalamuts drop them -- 30 % each -- only to whoever is
        /// on that step and until he holds six:
        /// «(Qs=1137&amp;PO&lt;11201,6)|(DD&gt;7,11198&amp;Qf=615&amp;PO!11201)|(Qs=1300&amp;PO!11201)».
        /// </summary>
        [Fact]
        public void The_vaccine_s_blood_drops_only_while_the_quest_wants_it()
        {
            if (!File.Exists(Paths.QuestsJson)) return;
            var book = new QuestCatalogue();
            if (book.Step(1137) == null) return;
            const string sample = "(Qs=1137&PO<11201,6)|(DD>7,11198&Qf=615&PO!11201)|(Qs=1300&PO!11201)";
            var log = new QuestLog(book, () => 100, () => 0);
            int held = 0;
            var facts = new PlayerCriteria.Facts(null, log, book, 0, _ => held);

            Assert.False(Met(sample, facts));   // without the quest, nothing
            log.Restore(615, 1137, null, false);
            Assert.True(Met(sample, facts));
            held = 5;
            Assert.True(Met(sample, facts));
            held = 6;
            Assert.False(Met(sample, facts));   // the six the doctor asks for, and no more
        }

        /// <summary>
        /// One judge for all of a monster's tables: the own table asks the fight's letters too (the
        /// Purple Dawn champion's temporal loop, «HA=51|HS=3384|HA=52|HS=3385», an elixir's state the
        /// winner carries), and the global one asks whether this is a soul-stone city-arena fight.
        /// </summary>
        [Fact]
        public void A_monster_s_tables_ask_the_fight_and_the_monster()
        {
            var session = GameSession.SinSocket();
            session.State.CharacterId = 9_400_000_011;
            session.State.MapId = 191105026;
            using (SessionContext.Push(session))
            {
                var fight = new Jondo.Unity.World.Fights.FightInstance(9_400_011, 191105026)
                {
                    SoulStoneFight = false,
                };
                var me = new Jondo.Unity.World.Fights.Fighter { Id = session.State.CharacterId, MaxHP = 100, CurrentHP = 100 };
                fight.AddPlayer(me);

                const string loop = "HA=51|HS=3384|HA=52|HS=3385";
                Assert.False(Jondo.Unity.Server.Handlers.FightHandler.SeLoLleva(fight, loop, _ => 0, 5662));
                me.Buffs.PonerEstado(3384);
                Assert.True(Jondo.Unity.Server.Handlers.FightHandler.SeLoLleva(fight, loop, _ => 0, 5662));

                Assert.True(Jondo.Unity.Server.Handlers.FightHandler.SeLoLleva(fight, "PZ=1&Ms=0", _ => 0, 98));
                Assert.True(Jondo.Unity.Server.Handlers.FightHandler.SeLoLleva(fight, "PZ=1&Ms=0")); // Ms is fight-wide.
                Assert.True(Jondo.Unity.Server.Handlers.FightHandler.SeLoLleva(fight, "PO<2269,10", item => item == 2269 ? 9 : 0));
                Assert.False(Jondo.Unity.Server.Handlers.FightHandler.SeLoLleva(fight, "PO<2269,10", item => item == 2269 ? 10 : 0));
            }
        }

        /// <summary>«QF&gt;q,0»: the quest finished at least once, as Cuerbax asks of «Proteger y servir».</summary>
        [Fact]
        public void A_quest_finished_once_is_counted()
        {
            if (!File.Exists(Paths.QuestsJson)) return;
            var book = new QuestCatalogue();
            if (book.Step(1237) == null) return;
            var log = new QuestLog(book, () => 200, () => 0);
            var facts = new PlayerCriteria.Facts(null, log, book, 0, _ => 0);
            Assert.False(Met("QF>632,0", facts));
            log.Restore(632, 1237, null, true);
            Assert.True(Met("QF>632,0", facts));
            Assert.True(Met("QF=632", facts));
            Assert.False(Met("QF>632,0&QF>633,0", facts));
        }

        /// <summary>Gein's own table: his follower, to whoever is on his quest's step.</summary>
        [Fact]
        public void A_monsters_quest_drop_keeps_its_criterion()
        {
            if (!File.Exists(Paths.WorldDb)) return;
            var drop = DatabaseManager.GetMonsterDrops(3761, 0).SingleOrDefault(d => d.ObjectId == 15585);
            if (drop == null) return;
            Assert.StartsWith("Qs=2174", drop.ReceiverCriterion);
        }
    }

    /// <summary>
    /// Ms writes maps into <see cref="MapManager.Maps"/>, so it runs with the other tests that do.
    /// </summary>
    [Collection("MapManager")]
    public class PlayerCriteriaArenaTests
    {
        private static bool Met(string criterion, PlayerCriteria.Facts facts)
            => Criterion.Met(criterion, condition => PlayerCriteria.Resolve(condition, facts));

        /// <summary>
        /// Mt is about the monster that drops; Ms is about a soul-stone fight in a city arena.
        /// </summary>
        [Fact]
        public void Ms_requires_fight_source_and_checks_the_Bonta_or_Brakmar_arena()
        {
            var nobody = new PlayerCriteria.Facts(null, null, null, 0, _ => 0);
            Assert.False(Met("Ms=0", nobody)); // the fight type is not known

            var regularFight = nobody with { SoulStoneFight = false };
            Assert.True(Met("Ms=0", regularFight));
            Assert.False(Met("Ms=1", regularFight));

            const long bontaArena = 987654321, brakmarArena = 987654322, otherMap = 987654323;
            var previousMaps = new Dictionary<long, MapInfo?>();
            foreach (long mapId in new[] { bontaArena, brakmarArena, otherMap })
                previousMaps[mapId] = MapManager.Maps.TryGetValue(mapId, out var previous) ? previous : null;
            try
            {
                MapManager.Maps[bontaArena] = new MapInfo { MapId = bontaArena, Name = "Arena de Bonta" };
                MapManager.Maps[brakmarArena] = new MapInfo { MapId = brakmarArena, Name = "Arène de Brâkmar" };
                MapManager.Maps[otherMap] = new MapInfo { MapId = otherMap, Name = "Amakna" };
                var soulStoneFight = regularFight with { MapId = bontaArena, SoulStoneFight = true };
                Assert.True(Met("Ms=1", soulStoneFight));
                Assert.True(Met("Ms>0", soulStoneFight));
                Assert.False(Met("Ms=0", soulStoneFight));

                var brakmarSoulStoneFight = soulStoneFight with { MapId = brakmarArena };
                Assert.True(Met("Ms=1", brakmarSoulStoneFight));
                Assert.False(Met("Ms=0", brakmarSoulStoneFight));

                var outsideArena = soulStoneFight with { MapId = otherMap };
                Assert.True(Met("Ms=0", outsideArena));
                Assert.False(Met("Ms=1", outsideArena));
            }
            finally
            {
                foreach (var (mapId, previous) in previousMaps)
                {
                    if (previous != null) MapManager.Maps[mapId] = previous;
                    else MapManager.Maps.Remove(mapId);
                }
            }

            // A map nobody registered: no metadata, so whether it is an arena is unknown.
            var unknownArena = regularFight with { MapId = 987654329, SoulStoneFight = true };
            Assert.False(Met("Ms=0", unknownArena)); // no map metadata, so arena state is unknown
        }
    }

    /// <summary>The criteria read off the calendar, run with the other tests that move its clock.</summary>
    [Collection(Jondo.Unity.Tests.Quests.AlmanaxTests.AlmanaxClock)]
    public class PlayerCriteriaCalendarTests
    {
        private static bool Met(string criterion, PlayerCriteria.Facts facts)
            => Criterion.Met(criterion, condition => PlayerCriteria.Resolve(condition, facts));

        [Fact]
        public void Uppercase_SD_uses_the_Paris_weekday_number_in_both_criterion_readers()
        {
            var clock = Almanax.Clock;
            try
            {
                // 12 November 2026 at noon UTC is Thursday afternoon in Paris.
                Almanax.Clock = () => new System.DateTime(2026, 11, 12, 12, 0, 0, System.DateTimeKind.Utc);

                var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => 0);
                Assert.True(Met("SD=5", facts));
                Assert.False(Met("SD=4", facts));
                Assert.True(Met("SD>4", facts));
                Assert.Equal(5L, new Achievements.CharacterFacts().Scalar("SD"));
            }
            finally
            {
                Almanax.Clock = clock;
            }
        }

        /// <summary>
        /// Sc only where its meaning is known: Halouine (967) is a yearly event, judged by the
        /// calendar.
        /// </summary>
        [Fact]
        public void A_known_Sc_is_judged_by_the_calendar()
        {
            var clock = Almanax.Clock;
            try
            {
                var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => 0);
                Almanax.Clock = () => new System.DateTime(2026, 10, 31, 12, 0, 0, System.DateTimeKind.Utc);
                bool halouine = SeasonalEvents.IsOn(967, Almanax.Clock());
                Assert.Equal(halouine, Met("Sc=967", facts));
                Assert.Equal(!halouine, Met("Sc!967", facts));
            }
            finally
            {
                Almanax.Clock = clock;
            }
        }
    }
}
