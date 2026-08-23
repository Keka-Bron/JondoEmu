using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Los susurros: hablar en privado con otro personaje.
    ///
    /// ─── Lo que va por el cable ─────────────────────────────────────────────────────────────
    ///
    /// El susurro NO es un canal más del chat normal. El chat de canal va en <c>ktm</c>, con el
    /// número del canal en su f3; el susurro tiene mensaje propio:
    ///
    ///   ktb { f1: el texto, f5: a quién }
    ///
    /// Medido en tres capturas, con esta forma exacta:
    ///
    ///   0a04 686f6c61  2200  2a0c 53616372692d4d6173746572
    ///   f1 = "hola"    f4 = ""    f5 = "Sacri-Master"
    ///
    /// El canal privado es el 9. Eso no lo hemos deducido: está en la propia tabla del cliente,
    /// ChatChannelsDataRoot, donde el 9 se llama «Privado», lleva <c>isPrivate</c> y su atajo es
    /// <c>/w</c>. En esa misma tabla el 10 es «Información» y el 11 «Combate», los tres privados.
    ///
    /// Cuando algo no se puede decir, el servidor contesta con <c>ktl</c>, que el volcado de
    /// nombres reales llama <c>ChatErrorEvent</c>, y lleva un solo número. El único valor que
    /// tenemos atado a una causa concreta es el 2, que es lo que contestó el servidor real al
    /// susurrarse a uno mismo. Los otros vistos —1, 4, 5, 8 y 10— salen al hablar por canales
    /// donde el jugador no puede, pero no se ha podido emparejar cada número con su motivo, así
    /// que aquí sólo se usa el 2, que sí está medido.
    ///
    /// ─── El mensaje NO es una línea de chat ─────────────────────────────────────────────────
    ///
    /// Esto costó un intento fallido: un susurro no se manda como un <c>kti</c> por el canal 9.
    /// Tiene mensaje propio, <c>kth</c> —ChatPrivateCopyMessageEvent en el volcado de nombres—, y
    /// el cliente lo reparte por el opcode, no por el canal. Mandarlo como kti canal 9 no pinta
    /// absolutamente nada: llega, el servidor lo da por hecho, y en pantalla no hay nada.
    ///
    ///   kth { f1: fecha, f4: vacío, f5: id del otro, f6: su nombre, f7: el texto }
    ///
    /// Y lo que lleva no es quién habla, sino EL OTRO: en la copia del que envía va a quién se lo
    /// dice. Está medido en la captura del gremio, donde el susurro a «Hiierbita-Xx» sí llegó a su
    /// destino y el servidor contestó con este kth.
    ///
    /// Del lado de QUIEN LO RECIBE no hay captura —haría falta grabar siendo el destinatario— así
    /// que se le manda el mismo kth con la identidad de quien habla. Es la lectura natural del
    /// formato: el campo es «el otro», y para el que recibe el otro es el que le escribe.
    /// </summary>
    public static class PrivateMessageHandler
    {
        /// <summary>El canal privado, de ChatChannelsDataRoot.</summary>
        public const int PrivateChannel = 9;

        /// <summary>
        /// Lo que contesta el servidor real cuando el susurro no sale. Medido susurrándose a uno
        /// mismo; para «ese personaje no está» no hay captura, así que se usa el mismo.
        /// </summary>
        public const int CannotWhisper = 2;

        public static async Task WhisperAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ktb = ConnectionProtocol.ReadPayload(payload, Op.Ktb);
            if (ktb == null) return;

            string text = "";
            string target = "";
            foreach (var field in ProtoMessage.Parse(ktb).Fields)
            {
                if (field.WireType != 2) continue;
                if (field.FieldNumber == 1) text = Text(field);
                else if (field.FieldNumber == 5) target = Text(field);
            }

            if (text.Length == 0 || target.Length == 0) return;

            string from = SessionContext.State.CharacterName;

            // A uno mismo no. Es justo el caso que hay medido: el servidor real contesta ktl 2.
            if (string.Equals(target, from, StringComparison.OrdinalIgnoreCase))
            {
                await RefuseAsync(stream);
                Console.WriteLine($"[Privado] {from} intenta susurrarse a sí mismo.");
                return;
            }

            var destino = SessionRegistry.FindByName(target);
            if (destino == null)
            {
                await RefuseAsync(stream);
                Console.WriteLine($"[Privado] {from} susurra a «{target}», que no está conectado.");
                return;
            }

            string cuando = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");

            // Al que lo recibe: el otro es quien le escribe.
            await destino.SendAsync(ConnectionProtocol.Push(Op.Kth,
                ConnectionProtocol.BuildPrivateMessage(
                    cuando, SessionContext.State.CharacterId, from, text)));

            // Y a quien lo manda, su copia: el otro es a quien se lo dice.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kth,
                    ConnectionProtocol.BuildPrivateMessage(
                        cuando, destino.State.CharacterId, destino.State.CharacterName, text)));

            Console.WriteLine($"[Privado] {from} → {destino.State.CharacterName}: {text}");
        }

        private static async Task RefuseAsync(NetworkStream stream)
            => await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ktl, ConnectionProtocol.BuildChatError(CannotWhisper)));

        private static string Text(ProtoField field)
        {
            try
            {
                return System.Text.Encoding.UTF8.GetString(field.BytesValue);
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
