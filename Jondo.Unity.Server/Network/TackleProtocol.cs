using System.Collections.Generic;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// What the wire carries when somebody is tackled leaving a cell. Measured on the seven
    /// tackles of the captures; see <see cref="Jondo.Unity.World.Fights.Tackle"/> for the figures.
    /// </summary>
    /// <remarks>
    /// All inside the walk's own sequence (jto 4), before the path it is paying for. The tutorial,
    /// frames 477-492:
    /// <code>
    ///   jto  { f1: mover, f2: 4 }
    ///   jwe  { f3: mover, f11 { f1: [tacklers] }, f14: 104 }      tackled, and by whom
    ///   jto 3, jxw { the AP left }, jwi 3
    ///   jwe  { f3: mover, f14: 101, f20 { f1: -AP lost, f2: mover } }
    ///   jto 3, jxw { the MP left }, jwi 3
    ///   jwe  { f3: mover, f14: 127, f20 { f1: -MP lost, f2: mover } }
    ///   jsj  { the path, the facing, mover }
    ///   jto 3, jxw { the MP left }, jwi 3
    ///   jwe  { f3: mover, f14: 129, f20 { f1: -steps, f2: mover } }
    ///   jwi  { .., mover, 4 }
    /// </code>
    /// A kind of point he loses none of is not announced at all: "entrar a combate-desconectarse"
    /// frames 2006-2017 lose one AP and no MP, and there is no 127 and no MP sheet before the jsj.
    /// The 101 and the 127 are the catalogue's "-#1 PA" and "-#1 PM", sent in the mover's own name.
    /// </remarks>
    public static class TackleProtocol
    {
        /// <summary>The action of being tackled (jwe f14). It is not in the effect catalogue.</summary>
        public const int Tackled = 104;

        /// <summary>
        /// <c>jwe { f3: mover, f11 { f1: packed tacklers }, f14: 104 }</c>. Byte for byte the
        /// tutorial's frame 478: "18df82c0feaa02 5a0c0a0affffffffffffffffff01 7068".
        /// </summary>
        public static byte[] BuildTackled(long mover, IEnumerable<long> tacklers)
            => Pb.New()
                .Var(3, mover)
                .Msg(11, Pb.New().Packed(1, tacklers))
                .Var(14, Tackled)
                .Build();
    }
}
