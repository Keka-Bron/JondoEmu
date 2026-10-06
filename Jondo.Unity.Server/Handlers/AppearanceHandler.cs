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
            await PreviewAsync(stream);
            await Task.CompletedTask;
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
                Console.WriteLine($"[Apariencias] La prenda {gid} no está en el catálogo.");
                return;
            }

            Wardrobe.Wear(Jondo.Unity.Server.Network.SessionContext.State.CharacterId, slot, VariantUid(gid, variant), gid);

            await PreviewAsync(stream);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lwz, Pb.New().Var(1, 1).Var(3, slot).Build(),
                                          ConnectionProtocol.RequestId(frame)));

            Console.WriteLine($"[Apariencias] Prenda {gid} (variante {variant}) al hueco {slot}.");
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
                Console.WriteLine($"[Apariencias] Hueco {slot} vaciado.");
            }
            else
            {
                Wardrobe.Wear(who, slot, VariantUid(gid, 0), gid);
                Console.WriteLine($"[Apariencias] Prenda {gid} al hueco {slot}.");
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
                    Console.WriteLine($"[Apariencias] Hueco {slot} {(ocultar ? "oculto" : "a la vista")}.");
                }
            }

            await PreviewAsync(stream);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lxk, Pb.New().Var(1, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>The aura, which is a subentity of attachment 6.</summary>
        public static async Task AuraAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lxw = ConnectionProtocol.ReadPayload(frame, Op.Lxw);
            int aura = 0;
            if (lxw != null)
            {
                foreach (var f in ProtoMessage.Parse(lxw).Fields)
                {
                    if (f.FieldNumber == 2 && f.WireType == 0) aura = (int)f.VarIntValue;
                }
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lym,
                    aura == 0 ? Array.Empty<byte>() : Pb.New().Var(1, aura).Build()));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lwx, Pb.New().Var(1, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));

            Console.WriteLine($"[Apariencias] Aura {aura}.");
        }

        /// <summary>
        /// The preview: only the lxc, which is what the panel sees. Nobody else finds out until it is
        /// saved.
        /// </summary>
        private static async Task PreviewAsync(NetworkStream stream)
        {
            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            if (character == null) return;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lxc, ConnectionProtocol.BuildLookChanged(character)));
        }

        /// <summary>
        /// Saving: this is where the look goes out to the world. The title and the ornament are saved
        /// with the same button, so this calls their part as well.
        /// </summary>
        public static async Task SaveAsync(NetworkStream stream, byte[] frame, long accountId)
        {
            await WardrobeHandler.SaveAsync(stream, frame, accountId);
        }

        /// <summary>
        /// An identifier for the garment worn. The window does not send an inventory uid -- it sends
        /// the template number --, so one is composed with the variant inside so as not to lose it.
        /// </summary>
        private static long VariantUid(int gid, int variant) => gid * 1000L + variant;
    }
}
