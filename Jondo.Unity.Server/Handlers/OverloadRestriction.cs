using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// An overloaded character cannot run: his client is told by his restrictions, the kur
    /// { f1: character, f2: restrictions } its played-character state handles, with «CannotRun».
    /// </summary>
    /// <remarks>
    /// His own client walks or runs on its own, from the click, before any answer of the server:
    /// the move's third field at 1 (GameMapMovementEvent.Pace) is what the others see, and the
    /// move's Cautious at 1 changed nothing for him. Told CannotRun, he walked -- tried in game
    /// against the official server's behaviour, «sobrecargado» walking and running again once under.
    /// Sent when the weight crosses the capacity, either way: after every pods refresh (iun) and
    /// every move, only when it changed.
    /// </remarks>
    public static class OverloadRestriction
    {
        /// <summary>The restriction «CannotRun» (lbd 4).</summary>
        public const int CannotRun = 4;

        /// <summary>
        /// «CannotAttackAsMutant» (lbd 11), every player's restriction who is not a mutant: the
        /// actor block's f7 is this one byte, 0x0b, in every capture.
        /// </summary>
        public const int CannotAttackAsMutant = 11;

        /// <summary>
        /// A player's restrictions: every player's, and CannotRun on top when overloaded. The
        /// whole list each time -- the kur that left only the 11 out, to let him run again, changed
        /// nothing, and the actor block's own list, sent on every map, put him back to running.
        /// </summary>
        public static long[] RestrictionsOf(bool overloaded)
            => overloaded ? new long[] { CannotRun, CannotAttackAsMutant } : new long[] { CannotAttackAsMutant };

        /// <summary>
        /// The same packed, for the actor block's f7: by his session's weight, the plain list without
        /// one. The block tells him too -- it goes to his own map --, so what it said is what he knows:
        /// left as it was, a map's block let him run again and the next step told him once more, the
        /// actor block with it, mid-walk.
        /// </summary>
        public static byte[] PackedOf(long characterId)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            bool overloaded = session?.State != null && IsOverloaded(session.State);
            if (session?.State != null) session.State.ToldOverloaded = overloaded;
            var packed = new System.Collections.Generic.List<byte>();
            foreach (long value in RestrictionsOf(overloaded)) packed.Add((byte)value);   // each under 128
            return packed.ToArray();
        }

        /// <summary>After a pods refresh written on a stream: its session's restriction, if it changed.</summary>
        internal static Task AfterPodsAsync(Stream stream)
        {
            var session = SessionRegistry.All().FirstOrDefault(s => s.Stream == stream);
            return session == null ? Task.CompletedTask : SyncAsync(session);
        }

        /// <summary>
        /// Tells the client he can or cannot run, when that changed since he was last told. With his
        /// actor block too, unless he is walking: the block carries his cell, and sent mid-walk with
        /// the walk's end in it, his client cancelled the walk and put him back.
        /// </summary>
        public static async Task SyncAsync(GameSession session, bool walking = false)
        {
            if (session?.State == null || !session.HasCharacter) return;
            bool overloaded = IsOverloaded(session.State);
            if (overloaded == session.State.ToldOverloaded) return;
            session.State.ToldOverloaded = overloaded;
            await session.SendAsync(ConnectionProtocol.Push("kur", Build(session.CharacterId, overloaded)));

            // And his actor block, with the same list: the kur alone set CannotRun but never took it
            // away, and he walked once lighter until the next map's block said otherwise.
            if (!walking && !session.State.IsInFight && DatabaseManager.GetCharacterById(session.CharacterId) is { } character)
                await session.SendAsync(ConnectionProtocol.Push(Jondo.Unity.Protocol.Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                    character, session.State.CellId, session.State.Orientation, session.AccountId)));
            Console.WriteLine($"[Pods] {session.CharacterId} {(overloaded ? "carries too much: cannot run" : "can run again")}.");
        }

        /// <summary>Whether he carries more than he can.</summary>
        public static bool IsOverloaded(SessionState state)
            => ConnectionProtocol.Carried(state) > ConnectionProtocol.Capacity(state);

        /// <summary>The kur: the character and his whole list of restrictions.</summary>
        public static byte[] Build(long characterId, bool overloaded)
            => Pb.New().Var(1, characterId).Packed(2, RestrictionsOf(overloaded)).Build();
    }
}
