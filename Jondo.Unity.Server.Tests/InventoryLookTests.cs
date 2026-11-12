using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class InventoryLookTests
{
    [Fact]
    public void Refresh_reads_the_owner_once_and_builds_distinct_current_preview_and_map_looks()
    {
        var owner = GameSession.SinSocket();
        owner.State.CharacterId = 71_001;
        var other = GameSession.SinSocket();
        other.State.CharacterId = 71_002;
        var character = new DbCharacter { Id = owner.CharacterId, Colors = new(0, null, null, null, null, 0xFFFFFF) };
        var reads = new List<bool>();
        int lookups = 0;
        using var scope = SessionContext.Push(other);
        var looks = InventoryHandler.BuildLooks(owner, id =>
        {
            lookups++;
            Assert.Equal(owner.CharacterId, id);
            Assert.Same(owner, SessionContext.Actual);
            return character;
        }, (source, preview) =>
        {
            Assert.Same(character, source);
            Assert.Same(owner, SessionContext.Actual);
            reads.Add(preview);
            return new([0x01000000, 0x06FFFFFF], 3, preview ? 200 : 900, [120], preview ? [800] : [], []);
        });
        Assert.Equal(1, lookups);
        Assert.Equal(new[] { true, false }, reads);
        Assert.Equal(200, looks.Preview.Bones);
        Assert.Equal(900, looks.Map.Bones);
        Assert.Equal(new[] { 800 }, looks.Preview.Skins);
        Assert.Empty(looks.Map.Skins);
        Assert.Same(other, SessionContext.Actual);
    }

    [Fact]
    public void Source_failures_restore_the_ambient_session_without_inventing_a_cached_look()
    {
        var owner = GameSession.SinSocket();
        owner.State.CharacterId = 71_001;
        var other = GameSession.SinSocket();
        using var scope = SessionContext.Push(other);
        Assert.Throws<InvalidOperationException>(() => InventoryHandler.BuildLooks(owner, _ => null,
            (_, _) => throw new Exception("No appearance without a character")));
        var failure = new IOException("appearance source failed");
        Assert.Same(failure, Assert.Throws<IOException>(() => InventoryHandler.BuildLooks(owner,
            _ => new() { Id = owner.CharacterId }, (_, _) => throw failure)));
        Assert.Same(other, SessionContext.Actual);
    }

    [Fact]
    public void Inventory_packets_serialize_their_own_semantic_look_at_the_wire_boundary()
    {
        var preview = new EntityLook([0x01112233], 3, 200, [120], [800], []);
        var map = new EntityLook([0x01445566], 3, 1, [55], [110, 2172, 5044], []);
        var local = ProtoMessage.Parse(ConnectionProtocol.ReadPayload(InventoryHandler.BuildLuqPacket(preview), Op.Luq)!);
        var localLook = ProtoMessage.Parse(local.Fields.Single(field => field.FieldNumber == 2).BytesValue);
        var actor = ProtoMessage.Parse(ConnectionProtocol.ReadPayload(InventoryHandler.BuildKkuPacket(map, 9_000_000_001), Op.Kku)!);
        var actorLook = ProtoMessage.Parse(actor.Fields.Single(field => field.FieldNumber == 1).BytesValue);
        Assert.Equal(new[] { 800 }, CharacterLookBuilderTests.Packed(localLook, 6));
        Assert.Equal(new[] { 110, 2172, 5044 }, CharacterLookBuilderTests.Packed(actorLook, 6));
        Assert.Equal(9_000_000_001, actor.Fields.Single(field => field.FieldNumber == 2).VarIntValue);
        Assert.Equal(3, actorLook.Fields.Single(field => field.FieldNumber == 2).VarIntValue);
    }
}
