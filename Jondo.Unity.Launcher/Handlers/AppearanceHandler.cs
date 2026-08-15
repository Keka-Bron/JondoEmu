using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// La ventana de apariencias: ponerse y quitarse prendas cosméticas.
    ///
    /// Funciona por BORRADOR, y eso es lo que hay que respetar para que se vea bien:
    ///
    ///   cliente  lyk                    abre la ventana
    ///   cliente  lyy { f1: uuid }       pide el estado         → servidor lxo
    ///   cliente  lys { f1: objeto, f2: variante }              → servidor lxc + lwz { f1:1, f3: hueco }
    ///   cliente  lyf { f2: objeto, f3: hueco }                 → servidor lxc + lyj { f3: 1 }
    ///   cliente  lyf { f3: hueco }      quitar del hueco       → servidor lxc + lyj { f3: 1 }
    ///   cliente  lxg { f1: hueco, f3: 1 }  ocultar             → servidor lxc + lxk { f1: 1 }
    ///   cliente  lxs                    GUARDAR                → servidor jsn + kmb + lxc, y lyu
    ///
    /// La diferencia entre los dos de poner: el `lys` deja que el servidor decida el hueco —y se lo
    /// devuelve en el `lwz`— y admite variante, que es lo que usan los "objevivos" para imitar una
    /// prenda u otra. El `lyf` dice el hueco directamente y no tiene variante.
    ///
    /// Y lo importante: mientras se toquetea, el servidor manda SOLO `lxc`, que es la vista previa
    /// del panel y no la ve nadie más. Hasta que no llega el `lxs` no salen el `jsn` ni el `kmb`,
    /// que son los que enseñan el aspecto nuevo al resto del mapa. Comprobado en las catorce
    /// capturas que acaban guardando.
    /// </summary>
    public static class AppearanceHandler
    {
        /// <summary>El lyy trae un uuid de personaje; el lxo devuelve uno de vista previa.</summary>
        private static string DraftIdOf(long characterId)
            => ConnectionProtocol.LookIdOf(characterId * 31 + 7);

        /// <summary>Abrir la ventana. El lyk va solo y no lleva respuesta propia.</summary>
        public static async Task OpenAsync(NetworkStream stream, long accountId)
        {
            await PreviewAsync(stream);
            await Task.CompletedTask;
        }

        /// <summary>El estado completo de la ventana.</summary>
        public static async Task SendStateAsync(NetworkStream stream, byte[] frame)
        {
            var character = DatabaseManager.GetCharacterById(GameState.CharacterId);
            if (character == null) return;

            await PreviewAsync(stream);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer("lxo",
                    ConnectionProtocol.BuildAppearanceState(character, DraftIdOf(character.Id)),
                    ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>Ponerse una prenda dejando que el servidor resuelva el hueco.</summary>
        public static async Task WearAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lys = ConnectionProtocol.ReadPayload(frame, "lys");
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

            Wardrobe.Wear(GameState.CharacterId, slot, VariantUid(gid, variant), gid);

            await PreviewAsync(stream);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer("lwz", Pb.New().Var(1, 1).Var(3, slot).Build(),
                                          ConnectionProtocol.RequestId(frame)));

            Console.WriteLine($"[Apariencias] Prenda {gid} (variante {variant}) al hueco {slot}.");
        }

        /// <summary>Poner o quitar en un hueco concreto. Sin objeto, se vacía.</summary>
        public static async Task AssignAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lyf = ConnectionProtocol.ReadPayload(frame, "lyf");
            if (lyf == null) return;

            int gid = 0, slot = 0;
            foreach (var f in ProtoMessage.Parse(lyf).Fields)
            {
                if (f.WireType != 0) continue;
                if (f.FieldNumber == 2) gid = (int)f.VarIntValue;
                else if (f.FieldNumber == 3) slot = (int)f.VarIntValue;
            }

            long who = GameState.CharacterId;
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
                ConnectionProtocol.Answer("lyj", Pb.New().Var(3, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// Enseñar u ocultar lo que hay en un hueco.
        ///
        ///   lxg { f1: hueco, f3: 1 }   ocultar
        ///   lxg { f1: hueco }          enseñar
        ///
        /// Sale de la captura de jugar con mostrar/ocultar: con el f3 puesto, la piel de ese hueco
        /// desaparece de la lista del lxc siguiente; sin él, vuelve. La prenda no se quita, solo
        /// deja de dibujarse.
        /// </summary>
        public static async Task ToggleAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lxg = ConnectionProtocol.ReadPayload(frame, "lxg");
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
                    Wardrobe.SetHidden(GameState.CharacterId, slot, ocultar);
                    Console.WriteLine($"[Apariencias] Hueco {slot} {(ocultar ? "oculto" : "a la vista")}.");
                }
            }

            await PreviewAsync(stream);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer("lxk", Pb.New().Var(1, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>El aura, que es una subentidad del enganche 6.</summary>
        public static async Task AuraAsync(NetworkStream stream, byte[] frame)
        {
            byte[]? lxw = ConnectionProtocol.ReadPayload(frame, "lxw");
            int aura = 0;
            if (lxw != null)
            {
                foreach (var f in ProtoMessage.Parse(lxw).Fields)
                {
                    if (f.FieldNumber == 2 && f.WireType == 0) aura = (int)f.VarIntValue;
                }
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("lym",
                    aura == 0 ? Array.Empty<byte>() : Pb.New().Var(1, aura).Build()));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer("lwx", Pb.New().Var(1, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));

            Console.WriteLine($"[Apariencias] Aura {aura}.");
        }

        /// <summary>
        /// La vista previa: solo el lxc, que es lo que ve el panel. Nadie más se entera hasta que
        /// se guarda.
        /// </summary>
        private static async Task PreviewAsync(NetworkStream stream)
        {
            var character = DatabaseManager.GetCharacterById(GameState.CharacterId);
            if (character == null) return;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("lxc", ConnectionProtocol.BuildLookChanged(character)));
        }

        /// <summary>
        /// Guardar: aquí es donde el aspecto sale al mundo. El título y el ornamento se guardan en
        /// el mismo botón, así que esto llama también a lo suyo.
        /// </summary>
        public static async Task SaveAsync(NetworkStream stream, byte[] frame, long accountId)
        {
            await WardrobeHandler.SaveAsync(stream, frame, accountId);
        }

        /// <summary>
        /// Un identificador para la prenda puesta. La ventana no manda uid de inventario —manda el
        /// número de plantilla—, así que se compone uno con la variante dentro para no perderla.
        /// </summary>
        private static long VariantUid(int gid, int variant) => gid * 1000L + variant;
    }
}
