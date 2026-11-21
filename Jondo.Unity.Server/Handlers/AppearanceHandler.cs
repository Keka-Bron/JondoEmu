using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The appearance window: putting on and taking off cosmetic garments.
    ///
    /// It works as a DRAFT, and that is what has to be respected for it to look right:
    ///
    ///   client  lyk                     opens the window
    ///   client  lyy { f1: uuid }        asks for the state       → server lxo
    ///   client  lys { f1: item, f2: variant }                    → server lxc + lwz { f1:1, f3: slot }
    ///   client  lyf { f2: item, f3: slot }                       → server lxc + lyj { f3: 1 }
    ///   client  lyf { f3: slot }        take off the slot        → server lxc + lyj { f3: 1 }
    ///   client  lxg { f1: slot, f3: 1 } hide                     → server lxc + lxk { f1: 1 }
    ///   client  lxs                     SAVE                     → server jsn + kmb + lxc, and lyu
    ///   client  lww { f1: 1 }           the draft switch         → server lxc + lwq { f1: outfit }
    ///   client  lwl { f1: head }        a face                   → server lxc + lye { f2: 1 }
    ///   client  lyo { f2: body }        a body                   → server lxc + lyz { f1: 1 }
    ///   client  lwu { f1: idle }        an idle animation        → server lwn { f2: 1 }
    ///   client  lyl { f1 { f2: colours } } the colours           → server lxc + lxd { f1: 1 }
    ///
    /// The outfits list -- putting one on, keeping, copying, renaming, throwing away -- is further down.
    ///
    /// The difference between the two ways of putting on: `lys` lets the server choose the slot --
    /// and gives it back in the `lwz` -- and accepts a variant, which is what the "living items" use
    /// to imitate one garment or another. `lyf` names the slot directly and has no variant.
    ///
    /// And the important part: while it is being fiddled with, the server sends ONLY `lxc`, which is
    /// the panel's preview and nobody else sees. Until the `lxs` arrives neither the `jsn` nor the
    /// `kmb` go out, which are the ones that show the new look to the rest of the map. Checked in the
    /// fourteen captures that end up saving.
    /// </summary>
    public static class AppearanceHandler
    {
        /// <summary>The lyy carries a character uuid; the lxo returns a preview one.</summary>
        private static string DraftIdOf(long characterId)
            => ConnectionProtocol.LookIdOf(characterId * 31 + 7);

        /// <summary>Opening the window. The lyk comes alone and has no answer of its own.</summary>
        public static async Task OpenAsync(NetworkStream stream, long accountId)
        {
            var session = SessionContext.Current;
            if (session != null)
            {
                session.State.IsWardrobeDraftLoaded = false;
                await WardrobeStateService.Live.SendOwnedAsync(session);
                var character = DatabaseManager.GetCharacterById(session.CharacterId);
                if (character != null)
                {
                    await session.SendAsync(ColorPaletteManager.BuildAppearanceEvent(character));
                }
            }
            await PreviewAsync(stream);
        }

        /// <summary>The window's whole state.</summary>
        public static async Task SendStateAsync(NetworkStream stream, byte[] frame)
        {
            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            if (character == null) return;

            await PreviewAsync(stream);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lxo,
                    ConnectionProtocol.BuildAppearanceState(character, DraftIdOf(character.Id)),
                    ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// The draft switch (lww): the window takes the outfit being drafted from the lwq's f1 and
        /// redraws it, with the preview look in front of it. Every change here is already a draft
        /// until the lxs, so the draft is the outfit as it stands.
        /// </summary>
        public static async Task DraftAsync(NetworkStream stream)
        {
            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            if (character == null) return;

            await PreviewAsync(stream);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lwq, ConnectionProtocol.BuildDraftOutfit(character, DraftIdOf(character.Id))));
        }

        /// <summary>
        /// Whether the account may wear that look: what its cosmetics collection holds -- the
        /// appearance window lists nothing else, and what is taken out of it while worn turns grey
        /// with its red mark. A mimisymbic's look is kept there too, under the real item's template.
        /// </summary>
        private static bool Owns(int gid)
            => CosmeticCollection.Has(Jondo.Unity.Server.Network.SessionContext.Current.AccountId, gid);

        /// <summary>Whether the account has unlocked that face, body or pose with its item (<see cref="AppearanceUnlocks"/>).</summary>
        private static bool Unlocked(AppearanceUnlocks.Kind kind, int id)
            => AppearanceUnlocks.Has(Jondo.Unity.Server.Network.SessionContext.Current.AccountId, kind, id);

        /// <summary>
        /// A face from the faces tab (lwl { f1: head }). Only the heads the tab offers with nothing
        /// unlocked -- the creation screen's, of the character's breed and sex
        /// (<see cref="HeadTable.OfferedFreely"/>) -- are taken; the answer is lye { f2: 1 }, which
        /// makes the client copy the head into its drafted outfit, and lye without it otherwise.
        /// INFERRED from the client: no capture changes a face.
        /// </summary>
        public static async Task FaceAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lwl = ConnectionProtocol.ReadPayload(frame, Op.Lwl);
            if (lwl == null) return;
            int head = (int)(ProtoMessage.Parse(lwl).Fields
                .FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 0)?.VarIntValue ?? 0);

            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            bool taken = character != null
                         && (HeadTable.OfferedFreely(head, character.Breed, character.Sex)
                             || Unlocked(AppearanceUnlocks.Kind.Head, head) && HeadTable.Fits(head, character.Breed, character.Sex))
                         && DatabaseManager.SetCharacterHead(character.Id, head);
            if (taken) await PreviewAsync(stream);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lye, Pb.New().VarIfNotZero(2, taken ? 1 : 0).Build(),
                                          ConnectionProtocol.RequestId(frame)));
            Console.WriteLine(taken ? $"[Appearances] Head {head}." : $"[Appearances] Head {head} is not offered.");
        }

        /// <summary>
        /// A body from the bodies tab (lyo { f2: body }), the first skin of the look: the breed's
        /// «base» or «retro» of the character's sex (<see cref="BodyTable.OfferedFreely"/>). The
        /// answer is lyz { f1: 1 }, which makes the client copy the body into its drafted outfit,
        /// and lyz without it otherwise. INFERRED from the client: no capture changes a body.
        /// </summary>
        public static async Task BodyAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lyo = ConnectionProtocol.ReadPayload(frame, Op.Lyo);
            if (lyo == null) return;
            int body = (int)(ProtoMessage.Parse(lyo).Fields
                .FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0);

            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            bool taken = character != null
                         && (BodyTable.OfferedFreely(body, character.Breed, character.Sex)
                             || Unlocked(AppearanceUnlocks.Kind.Body, body) && BodyTable.SkinFor(body, character.Breed, character.Sex) > 0);
            if (taken)
            {
                Wardrobe.SaveBody(character!.Id, body);
                await PreviewAsync(stream);
            }
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lyz, Pb.New().VarIfNotZero(1, taken ? 1 : 0).Build(),
                                          ConnectionProtocol.RequestId(frame)));
            Console.WriteLine(taken ? $"[Appearances] Body {body}." : $"[Appearances] Body {body} is not offered.");
        }

        /// <summary>
        /// An idle animation from the idles tab (lwu { f1: idle }): the plain one, or the breed's
        /// known one (<see cref="IdleTable.OfferedFreely"/>). The answer is lwn { f2: 1 }, which
        /// makes the client copy it into its drafted outfit, and lwn without it otherwise. The
        /// choice is kept and travels in the map look's f2 (see CharacterLookBuilder.Resolve), so
        /// the character is redrawn with it. INFERRED from the client: no capture changes an idle.
        /// </summary>
        public static async Task IdleAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lwu = ConnectionProtocol.ReadPayload(frame, Op.Lwu);
            if (lwu == null) return;
            int idle = (int)(ProtoMessage.Parse(lwu).Fields
                .FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 0)?.VarIntValue ?? 0);

            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            bool taken = character != null
                         && (IdleTable.OfferedFreely(idle, character.Breed)
                             || Unlocked(AppearanceUnlocks.Kind.Idle, idle) && IdleTable.Fits(idle, character.Breed));
            if (taken) Wardrobe.SaveIdle(character!.Id, idle);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lwn, Pb.New().VarIfNotZero(2, taken ? 1 : 0).Build(),
                                          ConnectionProtocol.RequestId(frame)));
            // The idle travels in the map look's f2: redrawn, the character plays it for everyone.
            if (taken) await SlotLookHandler.RedrawAsync(stream);
            Console.WriteLine(taken ? $"[Appearances] Idle {idle}." : $"[Appearances] Idle {idle} is not offered.");
        }

        /// <summary>
        /// The colours tab (lyl { f1 lvd { f2: the colours } }): the breed's palette, slot by slot,
        /// -1 for the breed's own colour -- the same lvd the outfit carries in its f1 --, kept in
        /// the six RGB columns as the creation's are. The answer is lxd { f1: 1 }, which makes the
        /// client copy the colours into its drafted outfit, and lxd without it for a palette that
        /// is not one (more colours than the breed has, or values past RGB). INFERRED from the
        /// client: no capture recolours a character.
        /// </summary>
        public static async Task ColorsAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? payload = ConnectionProtocol.ReadPayload(frame, Op.Lyl)
                           ?? ConnectionProtocol.ReadPayload(frame, Op.Lvl)
                           ?? ConnectionProtocol.ReadPayload(frame, Op.Lvp);
            if (payload == null) return;

            var parsed = ProtoMessage.Parse(payload);
            int slotIndex = -1;
            var colors = new List<long>();

            var slotField = parsed.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 0);
            if (slotField != null) slotIndex = (int)slotField.VarIntValue;

            var lvd = parsed.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2);
            if (lvd != null)
            {
                colors.AddRange(BankHandler.PackedOrSingle(ProtoMessage.Parse(lvd.BytesValue).Fields, 2)
                                           .Select(c => (long)(int)c));
            }
            else
            {
                var colorFields = BankHandler.PackedOrSingle(parsed.Fields, 2);
                if (colorFields.Count == 0)
                    colorFields = BankHandler.PackedOrSingle(parsed.Fields, 1);
                colors.AddRange(colorFields.Select(c => (long)(int)c));
            }

            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            int slots = character == null ? 0 : 6;
            bool taken = character != null && colors.Count > 0 && colors.Count <= slots
                         && colors.TrueForAll(c => c >= -1 && c <= 0xFFFFFF);
            if (taken)
            {
                SessionContext.State.WardrobeDraftColors = colors;
                if (slotIndex >= 0)
                {
                    SessionContext.State.WardrobeDraftPaletteIndex = slotIndex;
                }
                await PreviewAsync(stream);
            }
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lxd, Pb.New().VarIfNotZero(1, taken ? 1 : 0).Build(),
                                          ConnectionProtocol.RequestId(frame)));
            Console.WriteLine(taken ? $"[Appearances] Draft colours {string.Join(",", colors)} (slot {slotIndex})." : "[Appearances] Colours refused.");
        }

        /// <summary>Putting on a garment and letting the server work out the slot.</summary>
        public static async Task WearAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lys = ConnectionProtocol.ReadPayload(frame, Op.Lys);
            if (lys == null) return;

            int gid = 0, variant = 0;
            foreach (var f in ProtoMessage.Parse(lys).Fields)
            {
                if (f.WireType != 0) continue;
                if (f.FieldNumber == 1) gid = (int)f.VarIntValue;
                else if (f.FieldNumber == 2) variant = (int)f.VarIntValue;
            }

            int slot = Cosmetics.SlotOf(gid, variant);
            if (gid == 0 || slot < 0)
            {
                Console.WriteLine($"[Appearances] Garment {gid} is not in the catalogue.");
                return;
            }
            if (!Owns(gid))
            {
                Console.WriteLine($"[Appearances] Garment {gid} is not in the account's collection.");
                return;
            }

            Wardrobe.Wear(Jondo.Unity.Server.Network.SessionContext.State.CharacterId, slot, VariantUid(gid, variant), gid);

            await PreviewAsync(stream);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lwz, Pb.New().Var(1, 1).Var(3, slot).Build(),
                                          ConnectionProtocol.RequestId(frame)));

            Console.WriteLine($"[Appearances] Garment {gid} (variant {variant}) to slot {slot}.");
        }

        /// <summary>Putting on or taking off in a specific slot. With no item, it is emptied.</summary>
        public static async Task AssignAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lyf = ConnectionProtocol.ReadPayload(frame, Op.Lyf);
            if (lyf == null) return;

            int gid = 0, slot = 0;
            foreach (var f in ProtoMessage.Parse(lyf).Fields)
            {
                if (f.WireType != 0) continue;
                if (f.FieldNumber == 2) gid = (int)f.VarIntValue;
                else if (f.FieldNumber == 3) slot = (int)f.VarIntValue;
            }

            long who = Jondo.Unity.Server.Network.SessionContext.State.CharacterId;
            if (gid == 0)
            {
                Wardrobe.TakeOff(who, slot);
                Console.WriteLine($"[Appearances] Slot {slot} emptied.");
            }
            else if (!Owns(gid))
            {
                Console.WriteLine($"[Appearances] Garment {gid} is not in the account's collection.");
                return;
            }
            else
            {
                Wardrobe.Wear(who, slot, VariantUid(gid, 0), gid);
                Console.WriteLine($"[Appearances] Garment {gid} to slot {slot}.");
            }

            await PreviewAsync(stream);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lyj, Pb.New().Var(3, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// Showing or hiding what is in a slot.
        ///
        ///   lxg { f1: slot, f3: 1 }   hide
        ///   lxg { f1: slot }          show
        ///
        /// It comes from the capture of playing with show/hide: with f3 set, that slot's skin
        /// disappears from the next lxc's list; without it, it comes back. The garment is not taken
        /// off, it just stops being drawn.
        /// </summary>
        public static async Task ToggleAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lxg = ConnectionProtocol.ReadPayload(frame, Op.Lxg);
            if (lxg != null)
            {
                int slot = -1;
                bool ocultar = false;
                foreach (var f in ProtoMessage.Parse(lxg).Fields)
                {
                    if (f.WireType != 0) continue;
                    if (f.FieldNumber == 1) slot = (int)f.VarIntValue;
                    else if (f.FieldNumber == 3) ocultar = f.VarIntValue != 0;
                }

                if (slot >= 0)
                {
                    Wardrobe.SetHidden(Jondo.Unity.Server.Network.SessionContext.State.CharacterId, slot, ocultar);
                    Console.WriteLine($"[Appearances] Slot {slot} {(ocultar ? "hidden" : "on show")}.");
                }
            }

            await PreviewAsync(stream);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lxk, Pb.New().Var(1, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// The aura (lxw { f2: aura }, empty for none): one of the character's aura emotes, kept.
        /// lym { f1: aura } tells the wearer, who draws it from the id; the others see it as the
        /// sub-entity at binding point 6 of the map look, so the character is redrawn for the map.
        /// Answered lwx { f1: 1 }, and lwx without it for an aura not one's own.
        /// </summary>
        public static async Task AuraAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lxw = ConnectionProtocol.ReadPayload(frame, Op.Lxw);
            int aura = lxw == null ? 0 : (int)NumberOf(lxw, 2);

            var state = Jondo.Unity.Server.Network.SessionContext.State;
            bool taken = aura == 0 || Emotes.IsAura(aura) && state.Emotes.Contains(aura);
            if (taken)
            {
                Wardrobe.SaveAura(state.CharacterId, aura);
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lym, ConnectionProtocol.BuildAura(aura)));
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lwx, Pb.New().VarIfNotZero(1, taken ? 1 : 0).Build(),
                                          ConnectionProtocol.RequestId(frame)));
            if (taken) await SlotLookHandler.RedrawAsync(stream);

            Console.WriteLine(taken ? $"[Appearances] Aura {aura}." : $"[Appearances] Aura {aura} is not one's own.");
        }

        // ─── Outfits ────────────────────────────────────────────────────────────
        //
        // The outfits list (OutfitListUi) and the frame's pending state each request leaves for its
        // answer, read off eql and eqq:
        //
        //   client  lyy { f1: uuid }                      a tile clicked   → server lxc + lxo { f1: 1, f3 }
        //   client  lwk { f1, f2: icon, f4: name, f6 lvd } a new one       → server lxb { f1: 1, f2: uuid, f3 }
        //   client  lzd { f1: icon, f2: uuid, f3: name }  a copy           → server lxb (eqq::bbgk clears +408)
        //   client  lxt { f1: name, f2: favourite, f3: uuid, f5: icon }    → server lxr { f1: 1 }
        //   client  lyr { f2: uuid }                      thrown away      → server lxl { f1: 1 }
        //
        // INFERRED from the client: the one capture with two outfits only lists them.

        /// <summary>
        /// Putting on an outfit (lyy { f1: uuid }), its tile clicked; the window sends the worn one's
        /// too. The lxo's handler (eqq::bbgm) looks the uuid up in its list -- «Invalid outfit to
        /// equip id» otherwise -- and marks it worn; without f1 it shows ui.cosmetic.error.outfitEquipped.
        /// </summary>
        public static async Task EquipAsync(GameSession owner, byte[] frame)
        {
            using var context = SessionContext.Push(owner);
            var stream = owner.Stream!;
            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            if (character == null) return;

            byte[]? lyy = ConnectionProtocol.ReadPayload(frame, Op.Lyy);
            string uuid = lyy == null ? "" : TextOf(lyy, 1);
            if (uuid.Length > 0 && uuid != Outfits.EquippedOf(character.Id))
            {
                if (!Outfits.Equip(character, uuid, Owns))
                {
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Answer(Op.Lxo, Array.Empty<byte>(), ConnectionProtocol.RequestId(frame)));
                    Console.WriteLine($"[Appearances] Outfit {uuid} cannot be put on.");
                    return;
                }

                // The title and ornament the window drafts are the new outfit's now.
                Jondo.Unity.Server.Network.SessionContext.State.IsWardrobeDraftLoaded = false;
                await WardrobeHandler.AnnounceAsync(owner);
                await SlotLookHandler.RedrawAsync(stream);
                Console.WriteLine($"[Appearances] Outfit {uuid} put on.");
            }
            await SendStateAsync(stream, frame);
        }

        /// <summary>
        /// A new outfit from the one being drafted (lwk { f1: its number, f2: icon, f4: name,
        /// f6 lvd { f2: colours } }), kept and not put on. The answer is lxb { f1: 1, f2: uuid,
        /// f3: the outfit }, which the client adds to its list; without f1 it shows
        /// ui.cosmetic.error.outfitCreation.
        /// </summary>
        public static async Task CreateOutfitAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lwk = ConnectionProtocol.ReadPayload(frame, Op.Lwk);
            if (lwk == null) return;
            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);

            Outfits.Outfit? outfit = null;
            if (character != null)
            {
                var look = Drafted(character);
                var lvd = ProtoMessage.Parse(lwk).Fields.FirstOrDefault(f => f.FieldNumber == 6 && f.WireType == 2);
                var colors = lvd == null ? new List<long>()
                    : BankHandler.PackedOrSingle(ProtoMessage.Parse(lvd.BytesValue).Fields, 2).Select(c => (long)(int)c).ToList();
                if (colors.Count > 0 && colors.Count <= 6
                    && colors.TrueForAll(c => c >= -1 && c <= 0xFFFFFF))
                    look = look with { Colors = CharacterLookBuilder.CreationColors(character.Breed, character.Sex, colors) };

                outfit = Outfits.Create(character.Id, TextOf(lwk, 4), (int)NumberOf(lwk, 2), look);
            }
            await AnswerNewOutfitAsync(stream, frame, character, outfit);
        }

        /// <summary>
        /// A copy of an outfit (lzd { f1: icon, f2: the outfit's uuid, f3: name }), from its tile or
        /// from the worn one's. Answered like a new one, lxb.
        /// </summary>
        public static async Task DuplicateOutfitAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lzd = ConnectionProtocol.ReadPayload(frame, Op.Lzd);
            if (lzd == null) return;
            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);

            Outfits.Outfit? outfit = null;
            if (character != null)
            {
                string source = TextOf(lzd, 2);
                var original = Outfits.Of(character.Id).FirstOrDefault(o => o.Uuid == source);
                var look = original == null ? null : original.Kept ?? Outfits.Worn(character);
                if (look != null) outfit = Outfits.Create(character.Id, TextOf(lzd, 3), (int)NumberOf(lzd, 1), look);
            }
            await AnswerNewOutfitAsync(stream, frame, character, outfit);
        }

        private static async Task AnswerNewOutfitAsync(NetworkStream stream, byte[] frame,
                                                       DbCharacter? character, Outfits.Outfit? outfit)
        {
            var lxb = Pb.New();
            if (character != null && outfit != null)
                lxb.Var(1, 1).Str(2, outfit.Uuid).Bytes(3, ConnectionProtocol.BuildOutfit(character, outfit));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lxb, lxb.Build(), ConnectionProtocol.RequestId(frame)));
            Console.WriteLine(outfit != null ? $"[Appearances] Outfit «{outfit.Name}» kept." : "[Appearances] Outfit not kept.");
        }

        /// <summary>
        /// An outfit's name, icon and favourite mark (lxt { f1: name, f2: favourite, f3: uuid,
        /// f5: icon }), from its edition, its star or its icon picker. eqq::bbgg copies them into
        /// the client's outfit on lxr { f1: 1 }.
        /// </summary>
        public static async Task UpdateOutfitAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lxt = ConnectionProtocol.ReadPayload(frame, Op.Lxt);
            if (lxt == null) return;
            long who = Jondo.Unity.Server.Network.SessionContext.State.CharacterId;

            bool done = Outfits.Update(who, TextOf(lxt, 3), TextOf(lxt, 1), (int)NumberOf(lxt, 5), NumberOf(lxt, 2) != 0);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lxr, Pb.New().VarIfNotZero(1, done ? 1 : 0).Build(),
                                          ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// Throwing an outfit away (lyr { f2: uuid }); the worn one stays. eqq::bbgl takes it out of
        /// the list on lxl { f1: 1 }, and shows ui.cosmetic.error.outfitDeletion without it.
        /// </summary>
        public static async Task DeleteOutfitAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lyr = ConnectionProtocol.ReadPayload(frame, Op.Lyr);
            if (lyr == null) return;
            long who = Jondo.Unity.Server.Network.SessionContext.State.CharacterId;

            bool done = Outfits.Delete(who, TextOf(lyr, 2));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lxl, Pb.New().VarIfNotZero(1, done ? 1 : 0).Build(),
                                          ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>The outfit being drafted: what is worn, with the title and ornament the window has picked.</summary>
        private static Outfits.Look Drafted(DbCharacter character)
        {
            var look = Outfits.Worn(character);
            var state = Jondo.Unity.Server.Network.SessionContext.State;
            return state.IsWardrobeDraftLoaded
                ? look with { Title = state.WardrobeDraftTitle, Ornament = state.WardrobeDraftOrnament }
                : look;
        }

        private static string TextOf(byte[] message, int field)
            => ProtoMessage.Parse(message).Fields.FirstOrDefault(f => f.FieldNumber == field && f.WireType == 2) is { } f
                ? Encoding.UTF8.GetString(f.BytesValue) : "";

        private static long NumberOf(byte[] message, int field)
            => ProtoMessage.Parse(message).Fields.FirstOrDefault(f => f.FieldNumber == field && f.WireType == 0)?.VarIntValue ?? 0;

        /// <summary>
        /// The preview: only the lxc, which is what the panel sees. Nobody else finds out until it is
        /// saved.
        /// </summary>
        private static async Task PreviewAsync(NetworkStream stream)
        {
            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            if (character == null) return;

            var draftColors = Jondo.Unity.Server.Network.SessionContext.State.WardrobeDraftColors;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lxc, ConnectionProtocol.BuildLookChanged(character, draftColors)));
        }

        /// <summary>
        /// An identifier for the garment worn. The window does not send an inventory uid -- it sends
        /// the template number --, so one is composed with the variant inside so as not to lose it.
        /// </summary>
        private static long VariantUid(int gid, int variant) => gid * 1000L + variant;
    }
}
