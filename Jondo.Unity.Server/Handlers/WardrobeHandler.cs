using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The appearance window: the title and the ornament.
    ///
    /// It works as a DRAFT. Nothing touched in there is applied until Save is pressed, and that shows
    /// in the protocol:
    ///
    ///   client   lze { f1: title }      or empty to take it off   → server lxa { f2: 1 }
    ///   client   lwm { f2: ornament }   or empty to take it off   → server lyv { f1: 1 }
    ///   client   lxs (empty, the Save button)                     → server hid, hif, jsn, lxc
    ///                                                               and back lyu { f1: 1 }
    ///
    /// Careful with the fields, which do not match: the title travels in the lze's f1 and the ornament
    /// in the lwm's f2. And both accept the EMPTY message, which is "none" -- not a zero inside --.
    ///
    /// The three acknowledgements go in root field 3, which is the answer one, repeating the request's
    /// identifier.
    /// </summary>
    public static class WardrobeHandler
    {
        private static void EnsureDraft()
        {
            if (SessionContext.State.IsWardrobeDraftLoaded) return;
            var draft = Wardrobe.Of(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            SessionContext.State.WardrobeDraftTitle = draft.Title;
            SessionContext.State.WardrobeDraftOrnament = draft.Ornament;
            SessionContext.State.IsWardrobeDraftLoaded = true;
        }

        /// <summary>The client picks a title in the window. Only the draft is touched.</summary>
        public static async Task ChooseTitleAsync(NetworkStream stream, byte[] frame)
        {
            EnsureDraft();

            byte[]? lze = ConnectionProtocol.ReadPayload(frame, Op.Lze);
            int title = Wardrobe.None;
            if (lze != null)
            {
                foreach (var f in ProtoMessage.Parse(lze).Fields)
                {
                    if (f.FieldNumber == 1 && f.WireType == 0) title = (int)f.VarIntValue;
                }
            }

            if (!Titles.HasTitle(title))
            {
                Console.WriteLine($"[Appearances] Title {title} is not in the catalogue.");
                return;
            }

            SessionContext.State.WardrobeDraftTitle = title;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lxa, Pb.New().Var(2, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>The same with the ornament, which travels in f2.</summary>
        public static async Task ChooseOrnamentAsync(NetworkStream stream, byte[] frame)
        {
            EnsureDraft();

            byte[]? lwm = ConnectionProtocol.ReadPayload(frame, Op.Lwm);
            int ornament = Wardrobe.None;
            if (lwm != null)
            {
                foreach (var f in ProtoMessage.Parse(lwm).Fields)
                {
                    if (f.FieldNumber == 2 && f.WireType == 0) ornament = (int)f.VarIntValue;
                }
            }

            if (!Titles.HasOrnament(ornament))
            {
                Console.WriteLine($"[Appearances] Ornament {ornament} is not in the catalogue.");
                return;
            }

            SessionContext.State.WardrobeDraftOrnament = ornament;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lyv, Pb.New().Var(1, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>The Save button. This is where the draft becomes what is worn.</summary>
        public static async Task SaveAsync(GameSession owner, byte[] frame)
        {
            using var context = SessionContext.Push(owner);
            EnsureDraft();

            long who = Jondo.Unity.Server.Network.SessionContext.State.CharacterId;
            Wardrobe.SaveTitle(who, SessionContext.State.WardrobeDraftTitle);
            Wardrobe.SaveOrnament(who, SessionContext.State.WardrobeDraftOrnament);

            if (SessionContext.State.WardrobeDraftColors != null && SessionContext.State.WardrobeDraftColors.Count > 0)
            {
                var draftColors = SessionContext.State.WardrobeDraftColors;
                DatabaseManager.SaveCharacterColors(who, CharacterColors.FromRgb(draftColors));
                var updatedChar = DatabaseManager.GetCharacterById(who);
                if (updatedChar != null)
                {
                    int paletteIdx = SessionContext.State.WardrobeDraftPaletteIndex;
                    if (paletteIdx < 0)
                    {
                        paletteIdx = ColorPaletteManager.FindMatchingPaletteIndex(who, draftColors);
                        if (paletteIdx < 0)
                        {
                            int unlocked = ColorPaletteManager.GetSlotsUnlocked(who);
                            var currentPalettes = ColorPaletteManager.GetPalettes(updatedChar);
                            if (currentPalettes.Count < unlocked)
                            {
                                paletteIdx = currentPalettes.Count;
                            }
                            else
                            {
                                paletteIdx = 0;
                            }
                        }
                    }
                    var paletteColors = Network.InitialPresentationMessages.Palette(CharacterLookBuilder.SavedColors(updatedChar));
                    ColorPaletteManager.SavePalette(who, paletteIdx, paletteColors);
                    await owner.SendAsync(ColorPaletteManager.BuildAppearanceEvent(updatedChar));
                }
                SessionContext.State.WardrobeDraftColors = null;
                SessionContext.State.WardrobeDraftPaletteIndex = -1;
            }

            if (CharacterRemodel.PendingOf(who) is CharacterRemodel.Change.Colors or CharacterRemodel.Change.Face)
            {
                CharacterRemodel.Clear(who);
            }

            await AnnounceAsync(owner);
            if (owner.Stream != null) await SlotLookHandler.RedrawAsync(owner.Stream);

            await owner.SendAsync(
                ConnectionProtocol.Answer(Op.Lyu, Pb.New().Var(1, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));

            Console.WriteLine($"[Appearances] Saved: title {SessionContext.State.WardrobeDraftTitle}, " +
                              $"ornament {SessionContext.State.WardrobeDraftOrnament}.");
        }

        /// <summary>
        /// What the client is told when something changes: the title, the ornament and the whole actor,
        /// which is what makes the name repaint with its frame.
        /// </summary>
        public static Task AnnounceAsync(GameSession owner) => WardrobeStateService.Live.AnnounceAsync(owner);

        /// <summary>Everything one has, sent once on entering the world.</summary>
        public static Task SendOwnedAsync(GameSession owner) => WardrobeStateService.Live.SendOwnedAsync(owner);
    }
}
