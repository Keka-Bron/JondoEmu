using System;
using System.Collections.Generic;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The quest messages, byte for byte against what Ankama's server actually sent.
    /// </summary>
    /// <remarks>
    /// Not "it parses" and not "it round-trips through my own reader", which would only prove the
    /// reader and the writer agree with each other. These are the exact payloads out of the
    /// Wireshark captures, and the test is that what this server builds is indistinguishable from
    /// what the real one built for the same quest, step and objectives.
    ///
    /// The two <c>idu</c> frames below are the same step 2249 of quest 1629, a moment apart, and
    /// between them the player finished objective 9655. That is what proves the meaning of the
    /// flag — it <em>leaves</em> the objective that gets done — and it is the one thing here that
    /// would have been guessed wrong: the obvious reading of a field called state is that 1 means
    /// finished.
    /// </remarks>
    public class QuestProtocolTests
    {
        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        [Fact]
        public void A_quest_starting_is_one_varint()
        {
            // Tutorial capture, quest 2545 starting.
            Assert.Equal("08f113", Hex(QuestProtocol.BuildQuestStarted(2545)));
        }

        [Fact]
        public void The_quest_from_the_dialogue_capture_starts_the_same_way()
        {
            // "Misiones\hablar con NPC y aceptar una mision": ief {2432}, right after the player
            // picks the reply on line 50071.
            Assert.Equal("088013", Hex(QuestProtocol.BuildQuestStarted(2432)));
        }

        [Fact]
        public void A_validated_step_names_the_quest_and_the_step()
        {
            // Tutorial capture: idz {quest 2545, step 3226}.
            Assert.Equal("08f113109a19", Hex(QuestProtocol.BuildStepValidated(2545, 3226)));
        }

        [Fact]
        public void A_step_with_one_objective_still_to_do()
        {
            // Tutorial capture: quest 1629, step 2249, objective 9655 outstanding.
            //   0a0f          f1, 15 bytes
            //     120a        f2, 10 bytes
            //       0a05        f1, 5 bytes: an objective
            //         10b74b      f2 = 9655
            //         2001        f4 = 1, meaning still to do
            //       10c911      f2 = 2249, the step
            //     18dd0c      f3 = 1629, the quest
            Assert.Equal("0a0f120a0a0510b74b200110c91118dd0c",
                Hex(QuestProtocol.BuildQuestStep(1629, 2249, new[] { 9655 }, Array.Empty<int>())));
        }

        [Fact]
        public void The_objective_that_gets_done_loses_its_flag()
        {
            // The same step a moment later in the same capture: 9655 is finished, so it appears
            // with no f4 at all, and 9656 has arrived carrying the flag.
            Assert.Equal("0a14120f0a0310b74b0a0510b84b200110c91118dd0c",
                Hex(QuestProtocol.BuildQuestStep(1629, 2249, new[] { 9655, 9656 }, new[] { 9655 })));
        }

        [Fact]
        public void Five_done_and_one_to_go_matches_the_capture()
        {
            // A longer step from the same capture: quest 1630, step 2250, five objectives finished
            // and one outstanding. It also settles the order — 10121 comes before 10016, which is
            // the step's own declaration order and not numeric — so the list cannot be sorted on
            // the way out.
            Assert.Equal(
                "0a2812230a0310d04b0a0310d54b0a0310f84b0a0310894f0a0310a04e0a0510864c200110ca1118de0c",
                Hex(QuestProtocol.BuildQuestStep(1630, 2250,
                    new[] { 9680, 9685, 9720, 10121, 10016, 9734 },
                    new[] { 9680, 9685, 9720, 10121, 10016 })));
        }

        [Fact]
        public void The_order_is_the_one_it_is_given()
        {
            // Built with Pb rather than ProtoMessage.ToByteArray, which sorts by field number. The
            // objectives are a repeated field and the client lists them in the order they arrive,
            // so a sort would silently reorder somebody's quest steps on screen.
            string forwards = Hex(QuestProtocol.BuildQuestStep(1, 2, new[] { 10, 20 }, Array.Empty<int>()));
            string backwards = Hex(QuestProtocol.BuildQuestStep(1, 2, new[] { 20, 10 }, Array.Empty<int>()));

            Assert.NotEqual(forwards, backwards);
        }

        // ─── The green mark over an NPC ───────────────────────────────────────────

        [Fact]
        public void One_actor_with_one_quest_to_offer()
        {
            // From the tutorial capture: map 241438721, actor -20001, quest 2511.
            //   0a0d          f1, 13 bytes
            //     1207          f2, 7 bytes: one actor
            //       1202cf13      f2, 2 bytes packed: varint 2511
            //       20e0...       f4: the actor id
            //     1881...       f3: the map
            string built = Hex(QuestProtocol.BuildQuestMarks(
                241438721, new[] { (-20001L, (IReadOnlyList<int>)new[] { 2511 }) }));

            Assert.Contains("cf13", built);        // quest 2511, packed
            Assert.Contains("81a09073", built);    // map 241438721
        }

        [Fact]
        public void The_captured_mark_is_rebuilt_byte_for_byte()
        {
            // Not "contains" this time: the whole frame, against the one Ankama's server sent.
            //
            // Taken from "hablar con NPC y aceptar una mision.pcapng", the S->C stream, frame 13 --
            // the iom that lands right after the ief that starts the quest. Map 219417090, one
            // actor, one quest offered.
            //
            // Worth pinning whole because of f4: the actor id is -20000, and NPC ids are NEGATIVE.
            // Every earlier test here asserts on a substring, so all of them would still pass if
            // the sign were dropped or the ten-byte encoding of a negative int64 came out short --
            // and the client would then have a mark for an actor that is not on the map, which
            // draws nothing at all and looks exactly like the server never sent anything.
            //
            //   0a16                          f1, 22 bytes
            //     120f                          f2, 15 bytes: one actor
            //       1202 fb12                     f2, packed: varint 2427
            //       20 e0e3feffffffffffff01       f4: -20000
            //     18 8294d068                   f3: map 219417090
            Assert.Equal("0a16120f1202fb1220e0e3feffffffffffff01188294d068",
                         Hex(QuestProtocol.BuildQuestMarks(
                             219417090, new[] { (-20000L, (IReadOnlyList<int>)new[] { 2427 }) })));
        }

        [Fact]
        public void The_first_npc_of_a_map_gets_the_id_the_capture_shows()
        {
            // The other half of the same frame. -20000 is not a number this project chose: it is
            // what the real server put in f4 for the first NPC of that map, and ActorIds hands out
            // PrimerNpc - position. If the two ever drift apart the mark goes to nobody.
            Assert.Equal(-20000L, ActorIds.NpcDelMapa(0));
            Assert.Equal(-20001L, ActorIds.NpcDelMapa(1));
            Assert.True(ActorIds.EsNpc(ActorIds.NpcDelMapa(0)));
        }

        [Fact]
        public void An_actor_with_nothing_is_still_named()
        {
            // The mark is taken away by sending the actor with an empty list, which is what 235 of
            // the 380 captured frames do. Leaving the actor out instead would say nothing about it
            // and the client would keep drawing the mark it was told about last time.
            string built = Hex(QuestProtocol.BuildQuestMarks(
                1, new[] { (-20001L, (IReadOnlyList<int>)System.Array.Empty<int>()) }));

            Assert.Contains("1200", built);   // f2, zero bytes: the empty quest list
            Assert.NotEqual("", built);
        }

        [Fact]
        public void Several_quests_on_one_actor_go_in_one_packed_field()
        {
            // Packed is how the captures carry them - one length-delimited field with the varints
            // end to end - not one field per quest. Written the other way the client reads one
            // quest and drops the rest.
            string built = Hex(QuestProtocol.BuildQuestMarks(
                1, new[] { (-20001L, (IReadOnlyList<int>)new[] { 26, 2546 }) }));

            Assert.Contains("12031af213", built);
        }

        [Fact]
        public void A_step_with_no_objectives_still_names_the_step_and_the_quest()
        {
            // 9 of the 457 captured frames carry no objective list. Building one must not produce
            // an empty body, or the client is told nothing at all about a quest it asked about.
            string built = Hex(QuestProtocol.BuildQuestStep(1629, 2249, Array.Empty<int>(), Array.Empty<int>()));

            Assert.Contains("c911", built);   // the step
            Assert.Contains("dd0c", built);   // the quest
        }
    }
}
