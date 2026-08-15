using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// La ventana de apariencia: el título y el ornamento.
    ///
    /// Funciona por BORRADOR. Nada de lo que se toca ahí dentro se aplica hasta que se pulsa
    /// Guardar, y eso se ve en el protocolo:
    ///
    ///   cliente  lze { f1: título }    o vacío para quitárselo   → servidor lxa { f2: 1 }
    ///   cliente  lwm { f2: ornamento } o vacío para quitárselo   → servidor lyv { f1: 1 }
    ///   cliente  lxs (vacío, el botón Guardar)                   → servidor hid, hif, jsn, lxc
    ///                                                              y de vuelta lyu { f1: 1 }
    ///
    /// Ojo con los campos, que no coinciden: el título viaja en el f1 del lze y el ornamento en el
    /// f2 del lwm. Y los dos aceptan el mensaje VACÍO, que es "ninguno" —no un cero dentro—.
    ///
    /// Los tres acuses van en el campo raíz 3, que es el de respuesta, repitiendo el identificador
    /// de la petición.
    /// </summary>
    public static class WardrobeHandler
    {
        /// <summary>Lo elegido en la ventana, que todavía no se ha aplicado.</summary>
        private static int _draftTitle;
        private static int _draftOrnament;
        private static bool _draftLoaded;

        private static void EnsureDraft()
        {
            if (_draftLoaded) return;
            (_draftTitle, _draftOrnament) = Wardrobe.Of(GameState.CharacterId);
            _draftLoaded = true;
        }

        /// <summary>El cliente elige un título en la ventana. Solo toca el borrador.</summary>
        public static async Task ChooseTitleAsync(NetworkStream stream, byte[] frame)
        {
            EnsureDraft();

            byte[]? lze = ConnectionProtocol.ReadPayload(frame, "lze");
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
                Console.WriteLine($"[Apariencias] El título {title} no está en el catálogo.");
                return;
            }

            _draftTitle = title;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer("lxa", Pb.New().Var(2, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>Lo mismo con el ornamento, que viaja en el f2.</summary>
        public static async Task ChooseOrnamentAsync(NetworkStream stream, byte[] frame)
        {
            EnsureDraft();

            byte[]? lwm = ConnectionProtocol.ReadPayload(frame, "lwm");
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
                Console.WriteLine($"[Apariencias] El ornamento {ornament} no está en el catálogo.");
                return;
            }

            _draftOrnament = ornament;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer("lyv", Pb.New().Var(1, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>El botón Guardar. Aquí es donde el borrador se convierte en lo puesto.</summary>
        public static async Task SaveAsync(NetworkStream stream, byte[] frame, long accountId)
        {
            EnsureDraft();

            long who = GameState.CharacterId;
            Wardrobe.SaveTitle(who, _draftTitle);
            Wardrobe.SaveOrnament(who, _draftOrnament);

            await AnnounceAsync(stream, accountId);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer("lyu", Pb.New().Var(1, 1).Build(),
                                          ConnectionProtocol.RequestId(frame)));

            Console.WriteLine($"[Apariencias] Guardado: título {_draftTitle}, ornamento {_draftOrnament}.");
        }

        /// <summary>
        /// Lo que se le cuenta al cliente cuando algo cambia: el título, el ornamento y el actor
        /// entero, que es lo que hace que el nombre se repinte con su marco.
        /// </summary>
        public static async Task AnnounceAsync(NetworkStream stream, long accountId)
        {
            var (title, ornament) = Wardrobe.Of(GameState.CharacterId);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("hid", ConnectionProtocol.BuildTitleUpdated(title)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("hif", ConnectionProtocol.BuildOrnamentUpdated(ornament)));

            var character = DatabaseManager.GetCharacterById(GameState.CharacterId);
            if (character == null) return;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("jsn", ConnectionProtocol.BuildActorRefreshed(
                    character, GameState.CellId, GameState.Orientation, accountId)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("lxc", ConnectionProtocol.BuildLookChanged(character)));
        }

        /// <summary>Todo lo que uno tiene, que se manda una vez al entrar al mundo.</summary>
        public static async Task SendOwnedAsync(NetworkStream stream, long accountId)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("hhy", ConnectionProtocol.BuildTitlesOwned(
                    Titles.All, Titles.AllOrnaments)));

            // Los conjuntos del vestuario. Sin esto la ventana de cosméticos suena pero no llega a
            // dibujarse: el cliente no tiene ningún conjunto que enseñar y se cae.
            var character = DatabaseManager.GetCharacterById(GameState.CharacterId);
            if (character != null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push("lyt", ConnectionProtocol.BuildOutfits(character)));
            }

            await AnnounceAsync(stream, accountId);

            Console.WriteLine($"[Apariencias] Ofrecidos {Titles.All.Count} títulos y " +
                              $"{Titles.AllOrnaments.Count} ornamentos.");
        }
    }
}
