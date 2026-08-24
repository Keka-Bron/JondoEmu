using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Character-selection deletion for protocol 3.6.10.10.
    ///
    /// Live capture 2026-08-21: <c>kwa { f2: characterId }</c>. The older pinned .proto
    /// described two booleans, so field 2 must be read from the actual packet rather than that
    /// stale declaration. The selection UI is refreshed with its normal kvi/kvd list contract.
    /// </summary>
    public static class CharacterDeletionHandler
    {
        public static async Task HandleAsync(NetworkStream stream, byte[] framePayload,
                                             long accountId, int serverId)
        {
            byte[]? kwa = ConnectionProtocol.ReadPayload(framePayload,
                                                         Op.CharacterDeletionRequestMessage);
            if (kwa == null || accountId <= 0 || serverId <= 0) return;

            long characterId = 0;
            foreach (var field in ProtoMessage.Parse(kwa).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 0)
                {
                    characterId = field.VarIntValue;
                    break;
                }
            }

            // The official UI sends this from character selection. Do not allow a malformed
            // in-world packet to erase the character currently being played.
            if (characterId <= 0 || SessionContext.Current.IsInWorld)
            {
                Console.WriteLine("[Characters] Rejected invalid or in-world character deletion request.");
                return;
            }

            if (!DatabaseManager.TryDeleteCharacter(characterId, accountId, serverId))
            {
                Console.WriteLine($"[Characters] Rejected deletion of character {characterId}: it is not owned by this session.");
                return;
            }

            var remaining = DatabaseManager.GetCharactersByAccountId(accountId, serverId);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.CharactersListMessage,
                    ConnectionProtocol.BuildCharactersList(remaining)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.CharactersListEndMessage));

            Console.WriteLine($"[Characters] Deleted character {characterId}; {remaining.Count} character(s) remain on server {serverId}.");
        }
    }
}
