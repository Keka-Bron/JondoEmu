using System;
using System.IO;
using System.Text;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol.Messages;

namespace Jondo.Unity.Launcher.Handlers
{
    public static class CharacterSelectionHandler
    {
        private static async Task SendGameMessage(NetworkStream stream, string typeName, byte[] payload)
        {
            byte[] packet =
                NetworkEnvelope.BuildGameNodePacket(
                    $"type.ankama.com/{typeName}",
                    payload
                );

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(
                stream,
                packet
            );

            Console.WriteLine(
                $"[Game Node] Sent {typeName} ({payload.Length} B)"
            );
        }

        public static async Task HandleGameNodeAuth361010(
            NetworkStream stream,
            byte[] payload,
            string payloadStr)
        {
            // kqz reçu

            // 1. kra
            await SendGameMessage(stream, "kra", Array.Empty<byte>());

            // 2. lqu
            await SendGameMessage(stream, "lqu", BuildLqu361010());

            // 3. hoy
            await SendGameMessage(
                stream,
                "hoy",
                Convert.FromHexString("081E100118013202667238C801")
            );

            // 4. kqu
            await SendGameMessage(
                stream,
                "kqu",
                Convert.FromHexString(
                    "0A1103070D1417697C7D7E88018F0191019601"
                )
            );

            // 5. mgq
            await SendGameMessage(
                stream,
                "mgq",
                Convert.FromHexString("10011801")
            );

            // 6. mgt
            await SendGameMessage(
                stream,
                "mgt",
                Convert.FromHexString("1200")
            );

            // 7. hpd
            await SendGameMessage(
                stream,
                "hpd",
                Convert.FromHexString("0801")
            );

            // 8. krs
            await SendGameMessage(stream, "krs", Array.Empty<byte>());

            Console.WriteLine(
                "[Game Node] 3.6.10.10 initial handshake sent"
            );
        }


        public static async Task HandleCharacterList361010(
            NetworkStream stream)
        {
            // On appellera cette méthode après krv.

            // kqp #1
            await SendGameMessage(
                stream,
                "kqp",
                Convert.FromHexString("08011001")
            );

            // kqp #2
            await SendGameMessage(
                stream,
                "kqp",
                Convert.FromHexString("0801")
            );

            // kqp #3
            await SendGameMessage(
                stream,
                "kqp",
                Array.Empty<byte>()
            );

            // Ensuite kvi = vraie liste des personnages.
            await SendGameMessage(
                stream,
                "kvi",
                BuildKvi361010()
            );

            // mgz
            await SendGameMessage(
                stream,
                "mgz",
                Convert.FromHexString("0880D2C205")
            );

            // jtg ensuite.
            // Pour le premier test, on pourra décider
            // si on le reproduit ou si on le génère proprement.
        }

        private static byte[] BuildLqu361010()
        {
            var msg = new ProtoMessage();

            // UTC+2 pendant ta capture officielle.
            msg.Fields.Add(new ProtoField
            {
                FieldNumber = 1,
                WireType = 0,
                VarIntValue = 120
            });

            msg.Fields.Add(new ProtoField
            {
                FieldNumber = 2,
                WireType = 0,
                VarIntValue =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            return msg.ToByteArray();
        }

        private static byte[] BuildKvi361010()
        {
            // Payload kvi capturé sur le client officiel 3.6.10.10.
            // TEMPORAIRE : contient les personnages de la capture officielle.
            // On le remplacera ensuite par une génération depuis la DB Jondo.

            const string hex =
                "0A4E0A45120C447261676F2D42757272696E1857223312021801322B0A18" +
                "E284EA0DB49CE81480B5991FA2929C23E088B92DB7DEFE37100318012A01" +
                "393208AB1BB61BEB07A202381210A582B0A3A0070A520A49120E44726167" +
                "6F6E2D4C616E63696572180622351200322F0A18E8B6960FA9FEDC13C182" +
                "851AAAAFA72795AB9E2CBFFA8032100318012A0137320C95199218E209CC03" +
                "CD03CE03381410A5828081E718";

            return Convert.FromHexString(hex);
        }
    }
}
