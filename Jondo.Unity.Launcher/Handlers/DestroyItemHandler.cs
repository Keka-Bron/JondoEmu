using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Destruir un objeto del inventario: botón derecho, destruir, aceptar.
    ///
    ///   cliente  iuw { f2 { f2: uid, f3: cuántos } }
    ///
    /// El cliente no quita nada por su cuenta: manda la petición y espera. Sin respuesta, el objeto
    /// se queda en su sitio, que es lo que pasaba —el iuw llevaba tiempo saliendo en el registro de
    /// mensajes sin atender.
    ///
    /// La respuesta es la misma que cuando un objeto se va de la bolsa por cualquier otro motivo:
    ///
    ///   ium { f1: uid }   se va          o   iua, si solo baja la cantidad
    ///   iun               el peso, que ahora es menor
    /// </summary>
    public static class DestroyItemHandler
    {
        public static async Task DestroyAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iuw = ConnectionProtocol.ReadPayload(payload, "iuw");
            if (iuw == null) return;

            long uid = 0;
            int quantity = 0;

            // Va envuelto: f2 { f2: uid, f3: cuántos }.
            foreach (var field in ProtoMessage.Parse(iuw).Fields)
            {
                if (field.FieldNumber != 2 || field.WireType != 2) continue;
                foreach (var inner in ProtoMessage.Parse(field.BytesValue).Fields)
                {
                    if (inner.WireType != 0) continue;
                    if (inner.FieldNumber == 2) uid = inner.VarIntValue;
                    else if (inner.FieldNumber == 3) quantity = (int)inner.VarIntValue;
                }
            }
            if (uid == 0) return;

            var item = Equipment.ByUid(uid);
            if (item == null)
            {
                Console.WriteLine($"[Inventario] Piden destruir {uid}, que no es nuestro.");
                return;
            }

            int destruye = quantity <= 0 || quantity >= item.Quantity ? item.Quantity : quantity;
            bool entero = destruye >= item.Quantity;

            if (!DatabaseManager.DestroyCharacterItem(GameState.CharacterId, uid, destruye)) return;
            Equipment.Remove(uid, destruye);

            if (entero)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push("ium", ConnectionProtocol.BuildItemGone(uid)));
            }
            else
            {
                // Sigue habiendo: se manda otra vez con la cantidad nueva.
                var queda = HavenBagStore.FromInventory(GameState.CharacterId, uid);
                if (queda != null)
                {
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(ChestHandler.ArrivesInBag,
                            ConnectionProtocol.BuildItemArrived(
                                ChestHandler.FieldOf(ChestHandler.ArrivesInBag), queda)));
                }
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("iun",
                    ConnectionProtocol.BuildPods(0, 1000 + 5L * GameState.StatStrength)));

            Console.WriteLine($"[Inventario] Destruido {destruye} de {uid}" +
                              (entero ? " (entero)." : "."));
        }
    }
}
