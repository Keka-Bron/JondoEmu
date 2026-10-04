using System.Collections.Generic;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Emotes and smileys on the wire.
    /// </summary>
    /// <remarks>
    /// Measured in the four captures of the <c>Emotes</c> folder, plus the creation captures for
    /// the starting list. The client's emote frame (class <c>ern</c> in Core) is where each of these
    /// lands. <c>khi</c> and <c>khj</c> are the emote added and taken away: the guild captures send
    /// them with 97, the guild banner emote, on founding a guild and on leaving it, and the client's
    /// handlers for them add the emote to the list and remove it.
    ///
    /// <code>
    ///   C  khl {1: emote}                         play it
    ///   S  khh {1: character, 3: emote,           it plays, to everybody on the map
    ///           5: account, 6: animation}
    ///   S  khn {1: [emotes]}                      the character's emotes, on entering the world
    ///   S  khi {1: emote}                         one learned
    ///   C  hov {2: smiley}                        a smiley over the head
    ///   S  hoc {2: character, 3: account, 4: smiley}
    ///   C  hor {5: smiley}  or empty              the mood smiley, set or cleared
    ///   S  hns {3: smiley}  or empty
    /// </code>
    /// </remarks>
    public static class EmoteProtocol
    {
        /// <summary>
        /// Somebody plays an emote (khh), sent to everybody on the map, the player included.
        /// </summary>
        /// <remarks>
        /// Byte for byte what the juggling capture sends: <c>khh {1: 302677754146, 3: 29,
        /// 5: 65924386, 6: "AnimEmoteJuggle"}</c>. f5 is the account: 65924386 is the same number the
        /// map's actor record carries in its account field for that character.
        /// </remarks>
        public static byte[] BuildPlayed(long characterId, int emoteId, long accountId, string animation)
            => Pb.New()
                .Var(1, characterId)
                .VarIfNotZero(3, emoteId)
                .VarIfNotZero(5, accountId)
                .StrIfNotEmpty(6, animation)
                .Build();

        /// <summary>The character's emotes (khn): f1, packed.</summary>
        public static byte[] BuildList(IEnumerable<int> emotes)
        {
            var ids = new List<long>();
            foreach (int id in emotes) ids.Add(id);
            return Pb.New().Packed(1, ids).Build();
        }

        /// <summary>
        /// One emote learned (khi): f1 the emote, as the guild founding capture sends it for 97.
        /// </summary>
        public static byte[] BuildLearned(int emoteId) => Pb.New().Var(1, emoteId).Build();

        /// <summary>A smiley over somebody's head (hoc), to everybody on the map.</summary>
        public static byte[] BuildSmiley(long characterId, long accountId, int smileyId)
            => Pb.New().Var(2, characterId).VarIfNotZero(3, accountId).Var(4, smileyId).Build();

        /// <summary>The mood smiley (hns): f3, or nothing at all when it is cleared.</summary>
        public static byte[] BuildMood(int smileyId) => Pb.New().VarIfNotZero(3, smileyId).Build();

        /// <summary>The emote asked for (khl): f1.</summary>
        public static int ReadPlay(byte[] khl) => (int)First(khl, 1);

        /// <summary>The smiley asked for (hov): f2.</summary>
        public static int ReadSmiley(byte[] hov) => (int)First(hov, 2);

        /// <summary>The mood asked for (hor): f5, zero when it is being cleared.</summary>
        public static int ReadMood(byte[] hor) => (int)First(hor, 5);

        private static long First(byte[] payload, int field)
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
