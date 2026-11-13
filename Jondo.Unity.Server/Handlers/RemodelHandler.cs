using System;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The name and sex change potions (<see cref="CharacterRemodel"/>):
    /// <code>
    ///   use a «Pócima de cambio de nombre» (effect 850)  → lqn popup 41, the potion spent
    ///   use a «Pócima de cambio de sexo»   (effect 852)  → lqn popup 44, the potion spent
    ///   either, with a change already waiting            → lqn popup 43, the potion stays
    ///   next selection, a sex change waiting             → done before the character loads
    ///   next entry, a name change waiting                → kvk { f1: the name }: the Rename window
    ///   client  lvw { f1: the new name }                 → lwd { f2: the name } -- or popup 40
    /// </code>
    /// kvk is the creation screen's name suggestion; the character windows' frame (fqb::bhqt) opens
    /// the Rename window with it. On lwd without f1 that frame (fqb::bhqu) renames the played
    /// character and closes the window; with f1 it shows that code's reason, whose texts are not
    /// known, so a name refused is told with the popup the client has for it, «¡Este nombre no
    /// está disponible!», and the window stays open for another. INFERRED: no capture uses one.
    /// </summary>
    public static class RemodelHandler
    {
        public const int NameEffect = 850, SexEffect = 852;

        /// <summary>The popups (type 4) of the client's info messages.</summary>
        private const int Popup = 4;
        private const int NameNextTime = 41, SexNextTime = 44, ChangeAlreadyWaiting = 43, NameNotAvailable = 40;


        private static Task SendAsync(NetworkStream stream, string op, byte[] body)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(op, body));

        /// <summary>A name or sex change potion used: false when the item is no such thing.</summary>
        public static async Task<bool> TryUseAsync(NetworkStream stream, Equipment.Item item)
        {
            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            var change = effects.Any(e => e.EffectId == NameEffect) ? CharacterRemodel.Change.Name
                       : effects.Any(e => e.EffectId == SexEffect) ? CharacterRemodel.Change.Sex
                       : CharacterRemodel.Change.None;
            if (change == CharacterRemodel.Change.None) return false;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            long character = SessionContext.State.CharacterId;
            if (!CharacterRemodel.Request(character, change))
            {
                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(Popup, ChangeAlreadyWaiting));
                return true;
            }
            await ItemUseHandler.SpendOneAsync(stream, item.Uid);
            await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(Popup,
                change == CharacterRemodel.Change.Name ? NameNextTime : SexNextTime));
            Console.WriteLine($"[Remodel] {SessionContext.State.CharacterName} will change {change} on the next connection.");
            return true;
        }

        /// <summary>Entering the world with a name change waiting: the Rename window, with the name as it is.</summary>
        public static async Task OnEnterWorldAsync(NetworkStream stream, DatabaseManager.DbCharacter character)
        {
            if (CharacterRemodel.PendingOf(character.Id) != CharacterRemodel.Change.Name) return;
            await SendAsync(stream, Op.Kvk, Pb.New().Str(1, character.Name).Build());
        }

        /// <summary>lvw { f1: the new name }: taken if a change is waiting and the name is free and well made.</summary>
        public static async Task RenameAsync(NetworkStream stream, byte[] payload, long accountId)
        {
            byte[]? lvw = ConnectionProtocol.ReadPayload(payload, "lvw");
            if (lvw == null) return;
            string name = ProtoMessage.Parse(lvw).Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2) is { } field
                ? Encoding.UTF8.GetString(field.BytesValue).Trim() : "";

            var state = SessionContext.State;
            if (CharacterRemodel.PendingOf(state.CharacterId) != CharacterRemodel.Change.Name) return;
            if (!CharacterNames.IsValid(name) || !DatabaseManager.RenameCharacter(state.CharacterId, name))
            {
                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(Popup, NameNotAvailable));
                Console.WriteLine($"[Remodel] {state.CharacterName} cannot be called «{name}».");
                return;
            }

            string before = state.CharacterName;
            CharacterRemodel.Clear(state.CharacterId);
            state.CharacterName = name;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer("lwd", Pb.New().Str(2, name).Build(), ConnectionProtocol.RequestId(payload)));
            await SlotLookHandler.RedrawAsync(stream);
            Console.WriteLine($"[Remodel] {before} is now called {name}.");
        }
    }
}
