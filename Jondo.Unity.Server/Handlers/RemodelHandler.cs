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
    /// The name, colour and sex change potions (<see cref="CharacterRemodel"/>, <see cref="ColorPaletteManager"/>):
    /// <code>
    ///   use a «Pócima de cambio de nombre»  (effect 850) → lqn popup 41, the potion spent
    ///   use a «Pócima de cambio de colores» (effect 851) → one more palette slot, the potion spent,
    ///                                                      the colours tab's collection sent again
    ///   use a «Pócima de cambio de sexo»    (effect 852) → lqn popup 44, the potion spent
    ///   name or sex, with a change already waiting       → lqn popup 43, the potion stays
    ///   colours, with all twenty slots open              → «No cumples los requisitos.», the potion stays
    ///   next selection, a sex change waiting             → done before the character loads
    ///   next entry, a name change waiting                → kvk { f1: the name }: the Rename window
    ///   client  lvw { f1: the new name }                 → lwd { f2: the name } -- or popup 40
    /// </code>
    /// The kind comes from the template's effects alone. The «ilimitado» colour potions carry the
    /// same 851 effect as the plain one and no data marks them as kept on use (their criteria tie
    /// them to special servers), so they are spent like it. A colour change takes effect at once in
    /// the appearance window, so the «next connection» popup 42 is not sent for it.
    /// kvk is the creation screen's name suggestion; the character windows' frame (fqb::bhqt) opens
    /// the Rename window with it. On lwd without f1 that frame (fqb::bhqu) renames the played
    /// character and closes the window; with f1 it shows that code's reason, whose texts are not
    /// known, so a name refused is told with the popup the client has for it, «¡Este nombre no
    /// está disponible!», and the window stays open for another. INFERRED: no capture uses one.
    /// </summary>
    public static class RemodelHandler
    {
        public const int NameEffect = 850, ColorsEffect = 851, SexEffect = 852;

        /// <summary>The popups (type 4) of the client's info messages.</summary>
        private const int Popup = 4;
        private const int NameNextTime = 41, SexNextTime = 44, ChangeAlreadyWaiting = 43, NameNotAvailable = 40;


        private static Task SendAsync(NetworkStream stream, string op, byte[] body)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(op, body));

        /// <summary>A name, colour or sex change potion used: false when the item is no such thing.</summary>
        public static async Task<bool> TryUseAsync(NetworkStream stream, Equipment.Item item)
        {
            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            bool colors = effects.Any(e => e.EffectId == ColorsEffect);
            var change = effects.Any(e => e.EffectId == NameEffect) ? CharacterRemodel.Change.Name
                       : effects.Any(e => e.EffectId == SexEffect) ? CharacterRemodel.Change.Sex
                       : CharacterRemodel.Change.None;
            if (change == CharacterRemodel.Change.None && !colors) return false;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            long character = SessionContext.State.CharacterId;
            if (change == CharacterRemodel.Change.None) return await UnlockPaletteAsync(stream, item, character);

            if (!CharacterRemodel.Request(character, change))
            {
                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(Popup, ChangeAlreadyWaiting));
                return true;
            }
            await ItemUseHandler.SpendOneAsync(SessionContext.Current, item.Uid);
            await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(Popup,
                change == CharacterRemodel.Change.Name ? NameNextTime : SexNextTime));
            Console.WriteLine($"[Remodel] {SessionContext.State.CharacterName} will change {change} on the next connection.");
            return true;
        }

        /// <summary>
        /// A colour change potion: one more palette slot for the appearance window. With every slot
        /// open the potion is refused and kept. The session's turn keeps two uses from racing.
        /// </summary>
        private static async Task<bool> UnlockPaletteAsync(NetworkStream stream, Equipment.Item item, long character)
        {
            if (ColorPaletteManager.GetSlotsUnlocked(character) >= ColorPaletteManager.TotalSlots)
            {
                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.RequirementsNotMet));
                return true;
            }
            if (!await ItemUseHandler.SpendOneAsync(SessionContext.Current, item.Uid)) return true;
            if (!ColorPaletteManager.UnlockSlot(character))
                Console.WriteLine($"[Remodel] {SessionContext.State.CharacterName} spent potion {item.Template} but no slot was unlocked.");

            if (DatabaseManager.GetCharacterById(character) is { } dbCharacter)
                await SessionContext.Current.SendAsync(ColorPaletteManager.BuildAppearanceEvent(dbCharacter));
            Console.WriteLine($"[Remodel] {SessionContext.State.CharacterName} has {ColorPaletteManager.GetSlotsUnlocked(character)} palette slots (potion {item.Template}).");
            return true;
        }

        /// <summary>Entering the world with a name change waiting: the Rename window, with the name as it is.</summary>
        public static async Task OnEnterWorldAsync(GameSession session, DbCharacter character)
        {
            if (CharacterRemodel.PendingOf(character.Id) != CharacterRemodel.Change.Name) return;
            await session.SendAsync(new Jondo.Unity.Protocol.Game.CharacterNameSuggestionEvent(character.Name));
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
