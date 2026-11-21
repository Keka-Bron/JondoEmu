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
        public const int NameEffect = 850;
        public const int ColorsEffect = 851;
        public const int SexEffect = 852;
        public const int FaceEffect = 853;
        public const int ClassEffect = 854;

        /// <summary>The popups (type 4) of the client's info messages.</summary>
        private const int Popup = 4;
        private const int NameNotAvailable = 40;
        private const int NameNextTime = 41;
        private const int ColorsNextTime = 42;
        private const int ChangeAlreadyWaiting = 43;
        private const int SexNextTime = 44;
        private const int FaceNextTime = 58;


        private static Task SendAsync(NetworkStream stream, string op, byte[] body)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(op, body));

        /// <summary>A remodel potion used: false when the item is no such thing.</summary>
        public static async Task<bool> TryUseAsync(NetworkStream stream, Equipment.Item item)
        {
            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            var change = (effects.Any(e => e.EffectId == NameEffect) || item.Template == 10860) ? CharacterRemodel.Change.Name
                       : (effects.Any(e => e.EffectId == ColorsEffect) || item.Template is 10861 or 27277 or 27454 or 27724 or 27725) ? CharacterRemodel.Change.Colors
                       : (effects.Any(e => e.EffectId == SexEffect) || item.Template is 10862 or 25754) ? CharacterRemodel.Change.Sex
                       : (effects.Any(e => e.EffectId == FaceEffect) || item.Template == 13518) ? CharacterRemodel.Change.Face
                       : (effects.Any(e => e.EffectId == ClassEffect) || item.Template is 16147 or 28361 or (>= 29124 and <= 29132)) ? CharacterRemodel.Change.Class
                       : CharacterRemodel.Change.None;
            if (change == CharacterRemodel.Change.None) return false;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            long character = SessionContext.State.CharacterId;

            if (change == CharacterRemodel.Change.Colors)
            {
                var dbChar = DatabaseManager.GetCharacterById(character);
                int currentUnlocked = ColorPaletteManager.GetSlotsUnlocked(character);
                if (currentUnlocked >= ColorPaletteManager.TotalSlots)
                {
                    await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, InfoMessages.FreeText,
                        "You have reached the maximum limit of 20 color palettes. You can modify any of your existing palettes in the Appearance menu (adjust its colors and click Validate)."));
                    return true;
                }

                bool isUnlimited = item.Template is 27277 or 27454 or 27724 or 27725;
                if (!isUnlimited)
                {
                    await ItemUseHandler.SpendOneAsync(SessionContext.Current, item.Uid);
                }

                int unlocked = 1;
                if (dbChar != null)
                {
                    unlocked = ColorPaletteManager.UnlockSlot(character, dbChar);
                    var appEvent = ColorPaletteManager.BuildAppearanceEvent(dbChar);
                    if (SessionContext.Current != null)
                    {
                        await SessionContext.Current.SendAsync(appEvent);
                    }
                }

                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(Popup, ColorsNextTime));
                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, InfoMessages.FreeText,
                    $"New color palette unlocked! You now have {unlocked} palettes available in the Appearance menu."));
                Console.WriteLine($"[Remodel] {SessionContext.State.CharacterName} unlocked color palette #{unlocked} (potion {item.Template}).");
                return true;
            }

            if (!CharacterRemodel.Request(character, change))
            {
                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(Popup, ChangeAlreadyWaiting));
                return true;
            }

            bool isUnlimitedPotion = item.Template is 27277 or 27454 or 27724 or 27725;
            if (!isUnlimitedPotion)
            {
                await ItemUseHandler.SpendOneAsync(SessionContext.Current, item.Uid);
            }

            int popupId = change switch
            {
                CharacterRemodel.Change.Name => NameNextTime,
                CharacterRemodel.Change.Sex => SexNextTime,
                CharacterRemodel.Change.Face => FaceNextTime,
                _ => 0
            };

            if (popupId > 0)
            {
                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(Popup, popupId));
            }

            if (change == CharacterRemodel.Change.Name)
            {
                await SendAsync(stream, Op.Kvk, Pb.New().Str(1, SessionContext.State.CharacterName).Build());
            }
            else if (change == CharacterRemodel.Change.Face)
            {
                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, InfoMessages.FreeText,
                    "Face change potion activated. You can choose your new face in the Appearance menu."));
            }
            else if (change == CharacterRemodel.Change.Class)
            {
                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, InfoMessages.FreeText,
                    "Class change potion activated. You can choose your new class on your next login."));
            }

            Console.WriteLine($"[Remodel] {SessionContext.State.CharacterName} will change {change} (potion {item.Template}).");
            return true;
        }

        /// <summary>Entering the world with a remodel change waiting.</summary>
        public static async Task OnEnterWorldAsync(GameSession session, DbCharacter character)
        {
            var pending = CharacterRemodel.PendingOf(character.Id);
            if (pending == CharacterRemodel.Change.Name)
            {
                await session.SendAsync(new Jondo.Unity.Protocol.Game.CharacterNameSuggestionEvent(character.Name));
            }
            else if (pending == CharacterRemodel.Change.Colors)
            {
                await SendAsync(session.Stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, InfoMessages.FreeText,
                    "You have a pending color change. Open the Appearance menu to choose your colors."));
            }
            else if (pending == CharacterRemodel.Change.Face)
            {
                await SendAsync(session.Stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, InfoMessages.FreeText,
                    "You have a pending face change. Open the Appearance menu to choose your new face."));
            }
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
