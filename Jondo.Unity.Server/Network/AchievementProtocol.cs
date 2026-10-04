using System.Collections.Generic;
using Jondo.Unity.World.Achievements;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The achievement messages, both ways.
    /// </summary>
    /// <remarks>
    /// Read off the captures, and tied to the client's own code where the captures are silent.
    /// The client's achievement frame (class <c>epj</c> in Core, whose one readable member is
    /// <c>DelayedClearAchievements</c>) takes exactly these server messages, and hands them to the
    /// achievement window under names that were not obfuscated:
    ///
    /// <code>
    ///   mft  the list, on entering the world                 OnAchievementList
    ///   mfu  one earned                                      OnAchievementFinished
    ///   mfs  one paid for                                    OnAchievementRewardSuccess
    ///   mfo  a category, with every objective's progress     OnAchievementDetailedList
    ///   mgb  the ones closest to being earned                OnAchievementAlmostFinishedDetailedList
    ///   mfg  one achievement, with its objectives            OnAchievementDetails
    /// </code>
    ///
    /// and its sender (<c>epf</c>) builds the five requests: <c>mga</c> the claim, <c>mff</c> a
    /// category, <c>mfm</c> one achievement, and the empty <c>mfe</c> and <c>mfp</c> the window
    /// sends as it opens.
    /// </remarks>
    public static class AchievementProtocol
    {
        /// <summary>
        /// An achievement has been earned (mfu).
        ///
        ///   f1 { f1: the CHARACTER's level, f2: character id, f3: achievement id }
        /// </summary>
        /// <remarks>
        /// The level is the character's and not the achievement's, which is the one thing here that
        /// would have been guessed wrong. The tutorial capture sends 1 and then 2, for a player who
        /// was levelling; the long-route capture sends 200 fifteen times, for achievements whose
        /// own declared levels are 30, 50, 110 and 140.
        /// </remarks>
        public static byte[] BuildEarned(int characterLevel, long characterId, int achievementId)
            => Pb.New().Msg(1, Entry(characterLevel, characterId, achievementId)).Build();

        /// <summary>
        /// An achievement has been paid for (mfs).
        ///
        ///   f2: 1
        ///   f4: achievement id
        /// </summary>
        /// <remarks>
        /// Only ever after the claim: in all nine captured frames it follows an <c>mga</c> and the
        /// character sheet it changed, never an <c>mfu</c> directly. The client files it under
        /// OnAchievementRewardSuccess. f2 is 1 in all nine and is passed rather than hard-coded:
        /// a field that only ever carries one value in the captures is a field whose meaning is
        /// not known.
        /// </remarks>
        public static byte[] BuildRewarded(int achievementId, int state = 1)
            => Pb.New().Var(2, state).Var(4, achievementId).Build();

        /// <summary>
        /// Every achievement the character has, on entering the world (mft).
        ///
        ///   f1 (repeated) { f2: character id, f3: achievement id }
        /// </summary>
        /// <remarks>
        /// Measured in six captures, up to 986 entries, and no entry carries a level there: those
        /// characters had been paid for everything. An achievement earned and not yet paid for is
        /// sent the way mfu sends it, with the character's level in f1 — INFERRED: it is the only
        /// thing that tells the two apart in this message, and the client uses the same inner
        /// record for both.
        /// </remarks>
        public static byte[] BuildList(long characterId, IEnumerable<(int Achievement, int UnclaimedLevel)> achievements)
        {
            var list = Pb.New();
            foreach (var (achievement, level) in achievements)
            {
                list.Msg(1, Entry(level, characterId, achievement));
            }

            return list.Build();
        }

        private static Pb Entry(int level, long characterId, int achievementId)
            => Pb.New().VarIfNotZero(1, level).Var(2, characterId).Var(3, achievementId);

        /// <summary>
        /// Every achievement of one category, with how far each objective has got (mfo).
        ///
        ///   f2 (repeated) { f2: achievement, f3 (repeated) { f1: objective, f2: out of, f4: done so far } }
        /// </summary>
        /// <remarks>
        /// Measured in <c>Chats\usando todos los chats</c>: the answer to <c>mff {40}</c> lists the
        /// 23 achievements of category 40, all of them, earned or not. An objective still to do
        /// carries f4 even at zero — the bytes are <c>20 00</c>, not an absent field — and one that
        /// is done carries no f4 at all.
        /// </remarks>
        public static byte[] BuildDetailedList(IEnumerable<(int Achievement, IReadOnlyList<ObjectiveProgress> Objectives)> achievements)
        {
            var list = Pb.New();
            foreach (var (achievement, objectives) in achievements)
            {
                list.Msg(2, Detailed(achievement, objectives));
            }

            return list.Build();
        }

        /// <summary>
        /// The achievements closest to being earned (mgb), for the window's summary.
        ///
        ///   f1 (repeated) { the same record as <see cref="BuildDetailedList"/> }
        /// </summary>
        public static byte[] BuildAlmostFinished(IEnumerable<(int Achievement, IReadOnlyList<ObjectiveProgress> Objectives)> achievements)
        {
            var list = Pb.New();
            foreach (var (achievement, objectives) in achievements)
            {
                list.Msg(1, Detailed(achievement, objectives));
            }

            return list.Build();
        }

        /// <summary>
        /// One achievement with its objectives (mfg). INFERRED: no capture opens one on its own;
        /// the shape is the client's (mfg carries one record of the kind mfo and mgb list).
        /// </summary>
        public static byte[] BuildDetails(int achievementId, IReadOnlyList<ObjectiveProgress> objectives)
            => Pb.New().Msg(1, Detailed(achievementId, objectives)).Build();

        private static Pb Detailed(int achievementId, IReadOnlyList<ObjectiveProgress> objectives)
        {
            var record = Pb.New().Var(2, achievementId);
            foreach (var objective in objectives)
            {
                var o = Pb.New().Var(1, objective.ObjectiveId).Var(2, objective.Maximum);

                // Explicit even at zero: the real server writes "20 00" for an objective not started.
                if (!objective.Done) o.Var(4, objective.Current);
                record.Msg(3, o);
            }

            return record;
        }

        /// <summary>The category the window asks for (mff): f1.</summary>
        public static int ReadCategory(byte[] mff) => (int)FirstVarInt(mff, 1);

        /// <summary>The achievement the window asks about (mfm): f1, or f2 when f1 is missing. INFERRED.</summary>
        public static int ReadDetailsRequest(byte[] mfm)
        {
            long id = FirstVarInt(mfm, 1);
            if (id <= 0) id = FirstVarInt(mfm, 2);
            return id > 0 && id <= int.MaxValue ? (int)id : 0;
        }

        /// <summary>
        /// The achievement the client wants paid for (mga): f1, or -1 for all of them.
        /// </summary>
        /// <remarks>
        /// The -1 is not a guess: five of the captures send <c>mga</c> with a varint of
        /// 18446744073709551615, which is what -1 looks like on the wire.
        /// </remarks>
        public static int ReadClaim(byte[] mga)
        {
            foreach (var field in ProtoMessage.Parse(mga).Fields)
            {
                if (field.FieldNumber != 1 || field.WireType != 0) continue;

                long value = field.VarIntValue;

                // Anything that does not fit in an int is the client's -1, not an id: achievement
                // ids stop at 9,062. Reading it as an unsigned number would ask for reward
                // number 18,446,744,073,709,551,615 and quietly do nothing.
                return value > 0 && value <= int.MaxValue ? (int)value : -1;
            }

            return -1;
        }

        private static long FirstVarInt(byte[] payload, int field)
        {
            if (payload == null || payload.Length == 0) return 0;
            foreach (var f in ProtoMessage.Parse(payload).Fields)
            {
                if (f.FieldNumber == field && f.WireType == 0) return f.VarIntValue;
            }

            return 0;
        }
    }
}
