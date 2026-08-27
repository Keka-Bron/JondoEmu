namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The two messages the server sends when an achievement is earned.
    /// </summary>
    /// <remarks>
    /// Read off the captures. Every id these carry across the 401 files is a real achievement, and
    /// the meanings line up with what the player was doing at the time — the tutorial earns
    /// "Primer tiempo" on finishing the quest that achievement names, and crossing Cania earns
    /// "Landas de Cania".
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
            => Pb.New()
                .Msg(1, Pb.New()
                    .Var(1, characterLevel)
                    .Var(2, characterId)
                    .Var(3, achievementId))
                .Build();

        /// <summary>
        /// An achievement is finished (mfs).
        ///
        ///   f2: 1
        ///   f4: achievement id
        /// </summary>
        /// <remarks>
        /// f2 is 1 in all eight captured frames. It is passed rather than hard-coded for the same
        /// reason the quest step's state is: a field that only ever carries one value in the
        /// captures is a field whose meaning is unknown, not a constant.
        /// </remarks>
        public static byte[] BuildFinished(int achievementId, int state = 1)
            => Pb.New().Var(2, state).Var(4, achievementId).Build();
    }
}
