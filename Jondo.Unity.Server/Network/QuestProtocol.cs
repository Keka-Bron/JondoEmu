using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Remaining incremental quest notifications and step replies. Journal, followed
    /// quests and map marks use QuestMessages and QuestStateService directly.
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
        /// A quest is done (ied).
        ///
        ///   f1: quest id
        ///
        /// Measured: every quest finished in the captures (the rat quest's 1633, the tutorial's 1629,
        /// 1630, 2502, 2511 and 2545, Vulkania's 724) closes with the lqn 56 naming it and then
        /// this. The client's quest frame (eps) takes the quest out of those under way and out of
        /// the followed box on it, and files it with the finished ones.
        /// </summary>
        public static byte[] BuildQuestValidated(int questId)
            => Pb.New().Var(1, questId).Build();

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
        /// An objective has been validated and its step goes on (ieb).
        ///
        ///   f1: quest id
        ///   f2: objective id
        /// </summary>
        public static byte[] BuildObjectiveValidated(int questId, int objectiveId)
            => Pb.New().Var(1, questId).Var(2, objectiveId).Build();

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
        /// <b>Where it differs from Ankama's server.</b> Theirs sends a growing prefix of the step's
        /// objectives — step 3183 declares four and the capture shows two, then four — so objectives
        /// appear as they become relevant. Which of them Ankama considers revealed is not in any data
        /// the client has; the only thing that says it is a written binding that waits for others
        /// (Quests.Shown holds those back). Every other objective goes out at once, because showing a
        /// player the rest of the list early is a smaller wrong than hiding one they need. The order
        /// is the step's own declaration order, which the captures match exactly.
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
