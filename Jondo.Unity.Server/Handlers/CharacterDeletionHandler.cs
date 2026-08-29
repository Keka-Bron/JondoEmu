using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Deleting a character, and making the client believe it.
    /// </summary>
    /// <remarks>
    /// Measured end to end in "crear personaje - borrar personaje.pcapng", which records one
    /// deletion. The client asks three times and the real server answers with five frames:
    ///
    /// <code>
    ///   C-&gt;S  kwa  10de82c08e8a02                       f2: the character id
    ///   C-&gt;S  kvu  08&lt;id&gt; 1220 "9cdab917cc3d5a4a33..."  f1: the id, f2: 32 hex characters
    ///   C-&gt;S  kvh  (empty)
    ///
    ///   S-&gt;C  kvn  0a06 "Vos-Xx"                        f1: the name of what is gone
    ///   S-&gt;C  kqp kqp kqp  kvi  jtg                     the list again
    ///   S-&gt;C  kvm  (empty)
    /// </code>
    ///
    /// Three requests, three answers, and getting that split wrong is visible: with all three
    /// replies hung off the kvu, clicking the bin did nothing at all, because the confirmation
    /// popup is waiting for the kvn that answers the kwa.
    ///
    /// <code>
    ///   kwa  -&gt;  kvn                    the name, and the popup opens
    ///   kvu  -&gt;  kqp kqp kqp  kvi  jtg  deleted, and here is the list again
    ///   kvh  -&gt;  kvm                    closed
    /// </code>
    ///
    /// The list goes back framed rather than as a bare kvi, for the same reason it does after a
    /// creation: the client replaces what it holds only when the whole set arrives. Sending the
    /// kvi alone after a creation is what once put the PREVIOUS character into the world.
    ///
    /// About those 32 hex characters. In the game a deletion is confirmed by typing something --
    /// the character's name, or the secret answer on the account -- and the client sends it hashed.
    /// This server stores no secret, so there is nothing to compare it against and it is not
    /// checked. What protects a character is that the id has to belong to the account on this
    /// session, which is enforced inside the transaction in
    /// <see cref="DatabaseManager.DeleteCharacter"/> rather than here. That is a real difference
    /// from the real server and it is written down: someone who can already log into the account
    /// can delete its characters without knowing the answer.
    /// </remarks>
    public static class CharacterDeletionHandler
    {
        /// <summary>
        /// The kwa: the player clicked the bin. Answers with the name, which opens the popup.
        /// </summary>
        /// <remarks>
        /// Nothing is deleted here and nothing is remembered either. The kvu that follows carries
        /// the id again, so there is no half-finished deletion held between the two, and no state
        /// for another socket to walk into.
        /// </remarks>
        public static async Task<bool> AskAsync(NetworkStream stream, byte[] framePayload,
                                                long accountId, int serverId)
        {
            long characterId = ReadCharacterId(framePayload);
            if (characterId <= 0 || accountId <= 0)
            {
                Console.WriteLine($"[Personajes] Petición de borrado sin id o sin cuenta " +
                                  $"(personaje {characterId}, cuenta {accountId}).");
                return false;
            }

            // Straight off the list of the account itself, so a character that is not on it has no
            // name to give back and the popup never opens.
            var owned = DatabaseManager.GetCharactersByAccountId(accountId, serverId);
            var target = owned.FirstOrDefault(character => character.Id == characterId);

            if (target == null)
            {
                Console.WriteLine($"[Personajes] El personaje {characterId} no es de la cuenta {accountId}.");
                return false;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kvn, Pb.New().Str(1, target.Name).Build()));

            Console.WriteLine($"[Personajes] Confirmación de borrado para {target.Name} ({characterId}).");
            return true;
        }

        /// <summary>The kvh: the client is done with the popup. The kvm closes it.</summary>
        public static Task CloseAsync(NetworkStream stream)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kvm));

        /// <summary>
        /// The kvu: confirmed. Deletes, then sends the list as it now stands.
        /// </summary>
        public static async Task<bool> DeleteAsync(NetworkStream stream, byte[] framePayload,
                                                   long accountId, int serverId)
        {
            long characterId = ReadCharacterId(framePayload);

            if (characterId <= 0)
            {
                Console.WriteLine("[Personajes] El borrado no traía id de personaje.");
                return false;
            }

            // Same guard as the selection, and for the same reason: the id comes off the wire. A
            // socket that has not presented its ticket arrives here with account zero, and zero
            // must delete nothing rather than fall through to a lookup that ignores the account.
            if (accountId <= 0)
            {
                Console.WriteLine($"[Personajes] Borrado sin cuenta resuelta (personaje " +
                                  $"{characterId}). No se ha presentado el ticket.");
                return false;
            }

            string name = DatabaseManager.DeleteCharacter(characterId, accountId);
            if (name.Length == 0)
            {
                Console.WriteLine($"[Personajes] No se borró el personaje {characterId}.");
                return false;
            }

            var characters = DatabaseManager.GetCharactersByAccountId(accountId, serverId);
            foreach (byte[] frame in ConnectionProtocol.CharacterListFrames(characters))
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

            Console.WriteLine($"[Personajes] Borrado {name} ({characterId}). Quedan " +
                              $"{characters.Count} en el servidor {serverId}.");
            return true;
        }

        /// <summary>
        /// The character id out of a kvu, or out of the kwa that comes just before it.
        /// </summary>
        /// <remarks>
        /// Two messages carry the same id in different fields -- field 1 in the kvu, field 2 in the
        /// kwa -- so both are read here rather than assuming the client always gets as far as the
        /// kvu.
        /// </remarks>
        internal static long ReadCharacterId(byte[] framePayload)
        {
            try
            {
                byte[]? deletion = ConnectionProtocol.ReadPayload(framePayload, Op.Kvu);
                int idField = 1;

                if (deletion == null)
                {
                    deletion = ConnectionProtocol.ReadPayload(framePayload, Op.Kwa);
                    idField = 2;
                }

                if (deletion == null || deletion.Length == 0) return 0;

                var message = ProtoMessage.Parse(deletion);
                return message.Fields.FirstOrDefault(
                    field => field.FieldNumber == idField && field.WireType == 0)?.VarIntValue ?? 0;
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[-] Error reading the id to delete: {ex.Message}");
                return 0;
            }
        }
    }
}
