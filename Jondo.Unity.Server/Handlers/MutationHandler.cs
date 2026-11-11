using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// A mutation given and taken away: the item in the mutation slot and everything it changes on
    /// the client -- the spells, the bar, the look, the sheet (<see cref="Mutations"/>).
    /// </summary>
    /// <remarks>
    /// Both sequences are the orbalantir's, measured in «orbalantir de meriana-teleport a todas las
    /// peleas epicas». Given, right after the map change: iua into the slot, hms, itg, lqn 21
    /// «Has conseguido» with 1 and the item, iun, kmb and kub. Taken away: hod { f1 17 }, hms and
    /// itg already the character's own, ium, kmb, kub, iun and lqn 22 «Has perdido» with 1 and the
    /// item.
    /// </remarks>
    public static class MutationHandler
    {
        /// <summary>
        /// Mutates the character in session with the mutation item <paramref name="template"/>:
        /// whatever mutation he had goes, the new one comes. False when the item makes no mutation
        /// or he is fighting.
        /// </summary>
        public static async Task<bool> GiveAsync(NetworkStream stream, int template)
        {
            var me = SessionContext.State;
            if (me.IsInFight || me.FightId != 0) return false;
            if (!DatabaseManager.TryGetItemTemplateEffects(template, out string json)
                || Mutations.Of(Equipment.ParseEffects(json)) == null)
            {
                Console.WriteLine($"[Mutations] Item {template} makes no mutation.");
                return false;
            }

            // One at a time: the old one goes quietly, the new one's messages say everything.
            foreach (var old in Equipment.All.Where(i => i.Position == Mutations.Slot).ToList())
                await AlterationHandler.RemoveSlotItemAsync(stream, me.CharacterId, old);

            var placed = await BonusItemHandler.PlaceAsync(stream, template, new[] { Mutations.Slot });
            if (placed == null) return false;
            var mutation = Mutations.Current();

            await SpellHandler.SendSpellsAndBarAsync(SessionContext.Current);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained, "1", template.ToString())));
            await ItemUseHandler.WeighAsync(stream);
            await LookChangedAsync(stream);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));

            Console.WriteLine($"[Mutations] {me.CharacterName} mutates into monster {mutation?.Monster} " +
                              $"(grade {mutation?.Grade}) with item {template}.");
            return true;
        }

        /// <summary>The character in session's mutation taken away. False when he has none.</summary>
        public static async Task<bool> EndAsync(NetworkStream stream)
        {
            var me = SessionContext.State;
            var item = Equipment.InSlot(Mutations.Slot);
            if (item == null) return false;
            if (!AlterationHandler.TakeSlotItem(me.CharacterId, item)) return false;
            Mutations.Current();

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hod, Pb.New().Var(1, Mutations.EndedMark).Build()));
            await SpellHandler.SendSpellsAndBarAsync(SessionContext.Current);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(item.Uid)));
            await LookChangedAsync(stream);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
            await ItemUseHandler.WeighAsync(stream);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemLost, "1", item.Template.ToString())));

            Console.WriteLine($"[Mutations] {me.CharacterName} is himself again (item {item.Template}).");
            return true;
        }

        /// <summary>The character's look as it is now (kmb), to him and to whoever is on his map.</summary>
        private static async Task LookChangedAsync(NetworkStream stream)
        {
            var me = SessionContext.State;
            var character = DatabaseManager.GetCharacterById(me.CharacterId);
            if (character == null) return;
            byte[] kmb = ConnectionProtocol.Push(Op.Kmb,
                Pb.New().Bytes(1, BreedLookTable.MapLook(character)).Var(2, me.CharacterId).Build());
            await WriteAsync(stream, kmb);
            await SessionRegistry.BroadcastToMapAsync(me.MapId, kmb, SessionContext.Current.Id);
        }

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}
