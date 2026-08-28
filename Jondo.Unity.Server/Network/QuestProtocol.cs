using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The three messages the server sends about quests, built the way the captures have them.
    /// </summary>
    /// <remarks>
    /// Every shape here was read off the 401 Wireshark captures rather than guessed, and the thing
    /// that makes them trustworthy is that they agree with a catalogue they know nothing about: in
    /// the 448 <c>idu</c> frames on record, the step named by the packet really belongs to the
    /// quest named by the packet — 448 times out of 448 — and the objectives really belong to that
    /// step, 1,479 times out of 1,479. Numbers pulled out of a misread layout do not do that.
    ///
    /// The capture that shows the whole exchange is
    /// <c>Misiones\hablar con NPC y aceptar una mision</c>: the client opens a dialogue on map
    /// 212863492, the server walks the conversation down to line 50071, the player picks the reply,
    /// and the server pushes <c>ief {2432}</c>. Quest 2432 is handed out by NPC 6617 on map
    /// 212863492 and its only step declares dialogId 50071. Three independent numbers, one story.
    ///
    /// <b>Built with <see cref="Pb"/> and never with ProtoMessage.ToByteArray</b>, which sorts
    /// fields by number: <see cref="BuildQuestStep"/> writes a repeated field and the order it goes
    /// out in is the order the client lists the objectives in.
    /// </remarks>
    public static class QuestProtocol
    {
        /// <summary>
        /// A quest has started (ief).
        ///
        ///   f1: quest id
        /// </summary>
        public static byte[] BuildQuestStarted(int questId)
            => Pb.New().Var(1, questId).Build();

        /// <summary>
        /// The whole journal in one message (idr): what is under way and what is done.
        ///
        ///   f1 (repeated) { f2: the objectives and the step, f3: quest id }   under way
        ///   f3 (repeated) { f1: 1, f2: quest id }                             finished
        ///   f4  packed varints                                              more finished ones
        ///
        /// No outer wrapper, unlike idu. What looks like one when reading the frame by hand is the
        /// envelope's own <c>Any.value</c>, and mistaking it for a field of the message would put
        /// the whole journal one level too deep.
        /// </summary>
        /// <remarks>
        /// This is the message that fills the quest window on entering the world, and it is the one
        /// that was still handing every player somebody else's journal. The captured block carries
        /// 261 <c>idu</c> frames AND one <c>idr</c> holding the same 261 inside it plus 548
        /// finished, so taking only the <c>idu</c> out of the replay changed nothing that the
        /// player could see: 261 in progress and 622 finished still arrived, none of them theirs,
        /// and every quest in Incarnam and Astrub showed as already done — which is also why no NPC
        /// carried a green mark and none of them would hand anything over.
        ///
        /// The shape is measured, not guessed: all 261 f1 ids and all 548 f3 ids are real quests,
        /// and the two counts are exactly what the client prints on its two tabs.
        ///
        /// <b>f4 is a second list of finished quests</b>, packed rather than one submessage each,
        /// and the arithmetic is what says so: the capture carries 548 in f3 and 74 in f4, and the
        /// client prints <em>Terminadas (622)</em>. 548 + 74 = 622, exactly. What separates the two
        /// lists is not known — none of the 74 appears in f3, and 13 of them are also in the
        /// in-progress list, so it is not simply "the rest of them" — so everything finished goes in
        /// f3 and f4 is left empty. A character of this server has nothing to put there anyway.
        /// </remarks>
        public static byte[] BuildJournal(
            IEnumerable<(int Quest, int Step, IReadOnlyList<int> Objectives, IReadOnlyCollection<int> Done)> doing,
            IEnumerable<int> finished)
        {
            var inner = Pb.New();

            foreach (var (quest, step, objectives, done) in doing)
            {
                var body = Pb.New();
                foreach (int objective in objectives)
                {
                    var entry = Pb.New().Var(2, objective);
                    if (!done.Contains(objective)) entry.Var(4, 1);
                    body.Msg(1, entry);
                }

                body.Var(2, step);
                inner.Msg(1, Pb.New().Msg(2, body).Var(3, quest));
            }

            foreach (int quest in finished)
            {
                inner.Msg(3, Pb.New().Var(1, 1).Var(2, quest));
            }

            inner.EmptyMsg(4);
            return inner.Build();
        }

        /// <summary>
        /// A step has been validated (idz).
        ///
        ///   f1: quest id
        ///   f2: step id
        /// </summary>
        /// <remarks>
        /// Seen 21 times, all in the tutorial capture, always right after the client has been told
        /// the objectives of that step and has said they are done. The pair is internally
        /// consistent every time: step 3163 with quest 2502, step 3226 with quest 2545.
        /// </remarks>
        public static byte[] BuildStepValidated(int questId, int stepId)
            => Pb.New().Var(1, questId).Var(2, stepId).Build();

        /// <summary>
        /// Which actors on a map have a quest to hand out (iom): the green mark over an NPC.
        ///
        ///   f1 { f2 (repeated) { f2: packed quest ids, f4: actor id }
        ///        f3: map id }
        /// </summary>
        /// <remarks>
        /// Measured over the 380 captured frames: every one of the 294 numbers they carry is a real
        /// quest id, all 294. And the mark going out is in there too — in the tutorial one actor
        /// arrives carrying [2511] and later the same actor arrives with an empty list, which is
        /// the moment the quest is taken. 235 of the 380 are empty, so an empty list is how the
        /// mark is cleared rather than something to leave out.
        ///
        /// An actor with nothing to offer is sent WITH an empty list on purpose. Leaving it out
        /// would say nothing about it, and the client would go on drawing the mark it was told
        /// about last time.
        /// </remarks>
        public static byte[] BuildQuestMarks(long mapId,
                                             IEnumerable<(long Actor, IReadOnlyList<int> Quests)> actors)
        {
            var inner = Pb.New();
            foreach (var (actor, quests) in actors)
            {
                var block = Pb.New();

                // Packed, which is how the captures carry them: one length-delimited field with the
                // varints end to end, not one field per quest.
                if (quests.Count > 0)
                {
                    var packed = new List<long>(quests.Count);
                    foreach (int quest in quests) packed.Add(quest);
                    block.Packed(2, packed);
                }
                else
                {
                    block.Bytes(2, Array.Empty<byte>());
                }

                block.Var(4, actor);
                inner.Msg(2, block);
            }

            inner.Var(3, mapId);
            return Pb.New().Msg(1, inner).Build();
        }

        /// <summary>
        /// Where a quest has got to (idu), in answer to the client's ieo.
        ///
        ///   f1 { f2 { f1 (repeated) { f2: objective id, f4: state }
        ///             f2: step id }
        ///        f3: quest id }
        /// </summary>
        /// <remarks>
        /// The nesting is not decoration and it is not a guess: it is what the 457 captured frames
        /// hold. The objectives sit inside the same submessage as the step id, which is what says
        /// they belong to the step rather than to the quest — and it is why moving a quest on has
        /// to clear the ticked objectives, since the client is never told about the ones belonging
        /// to a step it is no longer on.
        ///
        /// <b>f4 means STILL TO DO, not done</b>, which is the opposite of the obvious reading and
        /// was worth measuring. Following step 2249 through the tutorial capture shows it plainly:
        ///
        /// <code>
        ///   [(9655, 1)]                                          9655 is what to do
        ///   [(9655, -), (9656, 1)]                               9655 done, now 9656
        ///   [(9655, -), (9656, -), (9657..9661, 1)]              both done, five more
        /// </code>
        ///
        /// The flag leaves an objective as it is finished. It is written as 1 rather than omitted
        /// for the ones outstanding, because proto3 drops zeroes and an objective with no f4 is
        /// exactly how the captures say "done".
        ///
        /// <b>One deliberate difference from Ankama's server.</b> Theirs sends a growing prefix of
        /// the step's objectives — step 3183 declares four and the capture shows two, then four —
        /// so objectives appear as they become relevant. This sends the step's whole list at once,
        /// because which of them Ankama considers revealed is not in any data the emulator has, and
        /// showing a player the rest of the list early is a smaller wrong than hiding an objective
        /// they need. The order is the step's own declaration order, which the captures match
        /// exactly.
        /// </remarks>
        public static byte[] BuildQuestStep(int questId, int stepId, IEnumerable<int> objectives,
                                            IReadOnlyCollection<int> done)
        {
            var inner = Pb.New();
            foreach (int objective in objectives)
            {
                var entry = Pb.New().Var(2, objective);

                // Only the outstanding ones carry the flag. VarIfNotZero would say the same thing,
                // but saying it this way round keeps the meaning on the surface.
                if (!done.Contains(objective)) entry.Var(4, 1);

                inner.Msg(1, entry);
            }

            inner.Var(2, stepId);

            return Pb.New()
                .Msg(1, Pb.New()
                    .Msg(2, inner)
                    .Var(3, questId))
                .Build();
        }
    }
}
