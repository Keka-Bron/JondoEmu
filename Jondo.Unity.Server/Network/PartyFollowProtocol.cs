using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The messages of following the party leader, byte for byte as the real server sends them.
    /// </summary>
    /// <remarks>
    /// Measured in "Grupos/con grupo seguir desplazamiento del lider en mapas cercanos y a traves
    /// de un zaap", recorded from the member's client (Harmoo leads, 293213045026; the member is
    /// 302677754146), and in "Combate/entrar a combate ... siguiendo a lider de grupo", same pair:
    ///
    /// <code>
    ///   C imh {}                         the member asks to follow           frames 0, 10, 269
    ///   S lqn { f2: 1662, f4: leader }   "Sigues el desplazamiento de..."
    ///   S ikv { leader, where he is }
    ///   S iln {}                         the answer, root field 3, request id -1
    ///
    ///   C imo {}                         the member stops following          frame 247
    ///   S lqn { f2: 1661, f4: leader }   "Ya no sigues el desplazamiento de..."
    ///   S ika { f1: the member himself }
    ///   S inb {}                         the answer, root field 3, request id -1
    ///
    ///   S imk { f1: 1 }  following on   -> the client sends imh              frames 9, 268
    ///   S imk {}         following off  -> the client sends imo              frame 245
    /// </code>
    ///
    /// The ikv is what moves the member. It goes out after every walk of the leader, right behind
    /// his jsj (1 us later, same segment), and after every map change of his, right behind the jsd
    /// and kmu the old map gets; the member's client answers with a jrw 4 to 14 ms later (87 ms
    /// once, frames 12-14), or as soon as the walk it is on ends, and walks on its own: to a
    /// cell next to the leader when he is on the same map, to the border and across it with
    /// the autopilot when he is not -- every jqk it sends while following carries
    /// f3 = 1, as the autopilot's do in "Movimiento/autopilotaje ponerlo y terminar", and a jqk
    /// by hand carries none. Nothing else starts that walk. Frame 5 is the leader's jsd and kmu
    /// with no ikv behind them, and the member does not move; frame 271 is an ikv alone, with the
    /// leader far away after a zaap, and the member crosses eighteen maps to reach him.
    /// </remarks>
    public static class PartyFollowProtocol
    {
        /// <summary>«Ya no sigues el desplazamiento de &lt;b&gt;{0}&lt;/b&gt;.» To the member, on imo.</summary>
        public const int StoppedFollowingMessage = 1661;

        /// <summary>«Sigues el desplazamiento de &lt;b&gt;{0}&lt;/b&gt;.» To the member, on imh.</summary>
        public const int FollowingMessage = 1662;

        /// <summary>
        /// «&lt;b&gt;{0}&lt;/b&gt; sigue tu desplazamiento.» To the leader. Measured from his side in
        /// "Grupos/invitar otro jugador a mi grupo y que acepte invitacion", frame 10, 335 ms
        /// after the ink: the time it takes the new member's client to ask to follow. It is not
        /// part of joining: "Sueños Infinitos/recibir invitacion a sueños" and the group search
        /// capture add members with the same ink and no 1663.
        /// </summary>
        public const int FollowsYouMessage = 1663;

        /// <summary>
        /// «&lt;b&gt;{0}&lt;/b&gt; ya no sigue tu desplazamiento.» To the leader, on imo. INFERRED: it
        /// is the other half of 1663 in the client's own table, but no capture records the
        /// leader's side of an imo.
        /// </summary>
        public const int StoppedFollowingYouMessage = 1664;

        /// <summary>
        /// Where the leader is (ikv):
        ///
        ///   f1: the leader
        ///   f3 { f1: map, f2: x, f4: subarea, f5: y }
        ///   f4: his cell
        ///
        /// The f3 block is the one the party sheet carries in its f4, and it was checked the same
        /// way: the six maps of the capture give in MapPositions exactly the x, y and subarea the
        /// messages carry. Negative coordinates travel as 64-bit two's complement, not zigzag.
        /// </summary>
        /// <remarks>
        /// The cell is the END of the leader's walk -- 46 after the jsj 406 -> 46 of frame 32 --
        /// and after a map change the cell he lands on: 538 on 120063490 in frame 103. A cell 0
        /// goes out without f4, which is what proto3 does with a zero; no capture shows one.
        /// </remarks>
        public static byte[] BuildLeaderPosition(long leaderId, long mapId, MapInfo? map, int cellId)
            => ConnectionProtocol.Push(Op.Ikv, Pb.New()
                .Var(1, leaderId)
                .Msg(3, MapPosition(mapId, map))
                .VarIfNotZero(4, cellId)
                .Build());

        /// <summary>
        /// A map and its place on the world map: { f1: map, f2: x, f4: subarea, f5: y }. Shared
        /// by the ikv and by the member sheet of the party. Without the map's data only the map
        /// goes, which is better than a made-up position.
        /// </summary>
        public static Pb MapPosition(long mapId, MapInfo? map)
        {
            var position = Pb.New().Var(1, mapId);
            if (map == null) return position;

            return position
                .VarIfNotZero(2, map.PosX)
                .VarIfNotZero(4, map.SubAreaId)
                .VarIfNotZero(5, map.PosY);
        }

        /// <summary>
        /// Following switched on or off (imk). Off travels with no payload at all -- proto3
        /// dropping a false -- and on as 0801.
        /// </summary>
        public static byte[] BuildFollowSwitch(bool on)
            => on
                ? ConnectionProtocol.Push(Op.Imk, Pb.New().Var(1, 1).Build())
                : ConnectionProtocol.Push(Op.Imk);

        /// <summary>The answer to imh (iln): empty, carrying back the request id.</summary>
        public static byte[] BuildFollowAnswer(long requestId)
            => ConnectionProtocol.Answer(Op.Iln, null, requestId);

        /// <summary>The answer to imo (inb): empty, carrying back the request id.</summary>
        public static byte[] BuildUnfollowAnswer(long requestId)
            => ConnectionProtocol.Answer(Op.Inb, null, requestId);

        /// <summary>
        /// A member no longer follows (ika): { f1: the member }. The capture has it going to that
        /// same member, with his own id, between the 1661 and the inb.
        /// </summary>
        public static byte[] BuildFollowEnded(long followerId)
            => ConnectionProtocol.Push(Op.Ika, Pb.New().Var(1, followerId).Build());

        /// <summary>«Sigues el desplazamiento de {leader}.»</summary>
        public static byte[] BuildFollowing(string leaderName)
            => Info(FollowingMessage, leaderName);

        /// <summary>«Ya no sigues el desplazamiento de {leader}.»</summary>
        public static byte[] BuildStoppedFollowing(string leaderName)
            => Info(StoppedFollowingMessage, leaderName);

        /// <summary>«{member} sigue tu desplazamiento.»</summary>
        public static byte[] BuildFollowsYou(string memberName)
            => Info(FollowsYouMessage, memberName);

        /// <summary>«{member} ya no sigue tu desplazamiento.»</summary>
        public static byte[] BuildStoppedFollowingYou(string memberName)
            => Info(StoppedFollowingYouMessage, memberName);

        private static byte[] Info(int messageId, string name)
            => ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(messageId, name ?? ""));
    }
}
