using System;
using System.IO;
using Google.Protobuf;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>Owner-scoped look construction and compatibility serializers retained independently of item movement.</summary>
    public static class InventoryHandler
    {
        internal static (EntityLook Preview, EntityLook Map) BuildLooks(GameSession session,
            Func<long, DbCharacter?> find, Func<DbCharacter, bool, EntityLook> build)
        {
            using var context = SessionContext.Push(session);
            var character = find(session.CharacterId)
                ?? throw new InvalidOperationException($"Character {session.CharacterId} no longer exists.");
            return (build(character, true), build(character, false));
        }

        internal static byte[] BuildLuqPacket(EntityLook look)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((2 << 3) | 2)); output.WriteBytes(ByteString.CopyFrom(CharacterLookBuilder.WriteLook(look).Build()));
            output.WriteTag((uint)((3 << 3) | 2)); output.WriteString(Guid.NewGuid().ToString());
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Luq), ms.ToArray());
        }

        internal static byte[] BuildKkuPacket(EntityLook look, long characterId)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 2)); output.WriteBytes(ByteString.CopyFrom(CharacterLookBuilder.WriteLook(look).Build()));
            output.WriteTag((uint)((2 << 3) | 0)); output.WriteInt64(characterId);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Kku), ms.ToArray());
        }
    }
}
