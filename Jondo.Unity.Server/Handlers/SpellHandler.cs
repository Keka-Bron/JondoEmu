using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Swapping a spell for its variant.
    ///
    /// Spells come in pairs and the character carries one of the two halves. When the player chooses the
    /// other -- from the panel or with a right click on the bar -- the client sends an hmt with the spell
    /// it wants, and the server answers two things:
    ///
    ///   iuq   for each slot of the bar that had the old half, with the new one inside
    ///   hng   the new spell and the grade the character's level gives it
    ///
    /// Taken from four real captures: absorción for furia and back, liberación for magnetismo and
    /// llamilla for llamita. In the magnetismo one two iuq came out because the old spell was on two
    /// slots of the bar, which confirms it goes one per slot and not one per swap.
    /// </summary>
    public static class SpellHandler
    {
        /// <summary>
        /// The spell list (hms) and the spell bar (itg) of the character's level, sent again: what
        /// a level-up leaves the client with, the new spells in the book and on the bar.
        /// </summary>
        /// <remarks>
        /// Only the .level command sent them. A level gained in a fight or from a reward showed its
        /// window and nothing else: the spells it opened reached neither the book nor the bar until
        /// the next login. With no spell table loaded nothing is sent: an empty hms does not say
        /// "nothing changed", it says "you have no spells".
        /// </remarks>
        internal static Task SendSpellsAndBarAsync(GameSession session)
            => SpellMessages.Live.RefreshAsync(session, () => ShortcutBarMessages.Live.SendSpellAsync(session));

        public static async Task HandleVariantAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? hmt = ConnectionProtocol.ReadPayload(payload, Op.Hmt);
            if (hmt == null) return;

            int wanted = 0;
            foreach (var field in ProtoMessage.Parse(hmt).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) wanted = (int)field.VarIntValue;
            }
            if (wanted == 0) return;

            var pair = SpellTable.PairOf(wanted);
            if (pair == null)
            {
                Console.WriteLine($"[Spells] The client asks for spell {wanted}, which pairs " +
                                  "with none. Nothing changes.");
                return;
            }

            int level = Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel;
            int grade = SpellTable.GradeFor(wanted, level);
            if (grade == 0)
            {
                Console.WriteLine($"[Spells] {wanted} needs more level than the character has " +
                                  $"({level}). Nothing changes.");
                return;
            }

            // The bar slot was held by the other half, which is the one leaving.
            int leaving = wanted == pair.Base ? pair.Variant : pair.Base;
            var slots = SpellChoices.SlotsHolding(leaving);

            SpellChoices.Choose(wanted);
            foreach (int slot in slots)
            {
                SpellChoices.PutInBar(slot, wanted);
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Iuq, ConnectionProtocol.BuildShortcutChanged(slot, wanted)));
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hng, ConnectionProtocol.BuildSpellSwapped(wanted, grade)));

            await FightHandler.RefreshPlayerSpellBarAsync(stream);

            Console.WriteLine($"[Spells] Pair {pair.Id}: {leaving} -> {wanted} (grade {grade}), " +
                              $"{slots.Count} bar slot(s) updated.");
        }
    }
}
