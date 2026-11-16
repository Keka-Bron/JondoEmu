using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World;

[Collection("guild raids")]
public sealed class HouseListFlowTests : IDisposable
{
    private const long Street = 212601864, Account = 8_967_000_001, Character = 9_967_000_001;
    private const int Door = 522653;
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"jondo-house-lists-{Guid.NewGuid():N}.db");
    private readonly List<object[]> _saved = [];

    public HouseListFlowTests()
    {
        MapManager.Initialize();
        Interactives.Initialize();
        Houses.Initialize();
        Assert.True(Houses.TryGetDoor(Street, Door, out _));
        HouseStore.Of(Street, Door, 1);
        using var db = Open();
        using var read = db.CreateCommand();
        read.CommandText = $"SELECT * FROM Houses WHERE MapId = {Street} AND ElementId = {Door};";
        using (var rows = read.ExecuteReader())
            while (rows.Read()) { var row = new object[rows.FieldCount]; rows.GetValues(row); _saved.Add(row); }
        Clear(db);
        GuildStore.ConnectionStringOverride = $"Data Source={_file}";
    }

    public void Dispose()
    {
        using (var db = Open())
        {
            Clear(db);
            foreach (var row in _saved)
            {
                using var insert = db.CreateCommand();
                insert.CommandText = $"INSERT INTO Houses VALUES ({string.Join(",", row.Select((_, i) => "$v" + i))});";
                for (int i = 0; i < row.Length; i++) insert.Parameters.AddWithValue("$v" + i, row[i]);
                insert.ExecuteNonQuery();
            }
            using var clean = db.CreateCommand();
            clean.CommandText = "DELETE FROM Houses WHERE AccountId = $account;";
            clean.Parameters.AddWithValue("$account", Account);
            clean.ExecuteNonQuery();
        }
        GuildStore.ConnectionStringOverride = null;
        TestDatabases.ReleasePool(_file);
        File.Delete(_file);
    }

    [Fact]
    public async Task Lists_use_explicit_owner_and_detached_full_instances_with_distinct_locks_rights_and_order()
    {
        var guild = GuildStore.Create(Character, "HouseGuild", 1, 2, 3, 4);
        Insert(2, Account, guild.Id, 505, 8_000_000_003, "1234");
        Insert(1, Account, guild.Id, 217, 0, "");
        // Unknown doors are skipped, retaining the valid entries in store order.
        using (var db = Open())
        using (var insert = db.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO Houses (MapId, ElementId, Instance, AccountId) VALUES (9999999999, 999999, 1, {Account});";
            insert.ExecuteNonQuery();
        }
        var codec = new RecordingCodec();
        await using var pipe = await ClientPipe.OpenAsync(Account, Character, Street, codec: codec);
        var wrong = GameSession.SinSocket();
        wrong.BindAccount(Account + 1, 1);
        wrong.State.CharacterId = Character + 1;
        using var context = SessionContext.Push(wrong);
        using var turn = await pipe.Session.EnterTurnAsync();
        await HouseHandler.SendAccountHousesAsync(pipe.Session).WaitAsync(TimeSpan.FromSeconds(5));
        await HouseHandler.SendGuildHousesAsync(pipe.Session).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0x71, 1 }, await Packet(pipe));
        Assert.Equal(new byte[] { 0x71, 2 }, await Packet(pipe));
        var account = Assert.IsType<HouseAccountEvent>(codec.Frames[0].Content);
        var shared = Assert.IsType<GuildHousesEvent>(codec.Frames[1].Content);
        Assert.Equal(new[] { 1, 2 }, account.Houses.Select(h => Assert.IsType<HouseInformationCaseHouseAccount>(h.InformationCase).Value.HouseInformation.InstanceId));
        Assert.Equal(new long[] { 217, 505 }, shared.Houses.Select(h => Assert.IsType<HouseInformationCaseHouseGuild>(h.InformationCase).Value.GuildShareParams));
        var first = Assert.IsType<HouseInformationCaseHouseAccount>(account.Houses[0].InformationCase).Value;
        var second = Assert.IsType<HouseInformationCaseHouseAccount>(account.Houses[1].InformationCase).Value;
        Assert.Equal(Houses.HouseIdOf(Street, Door), account.Houses[0].HouseId);
        Assert.Equal(new MapExtendedCoordinates(Street, -30, 973, -53), first.Coordinates);
        Assert.Null(first.HouseInformation.Price);
        Assert.False(first.HouseInformation.IsAdminLocked);
        Assert.True(first.HouseInformation.IsLocked);
        Assert.True(second.HouseInformation.IsAdminLocked);
        Assert.True(second.HouseInformation.IsLocked);
        Assert.False(second.HouseInformation.IsSaleLocked);
        Assert.Equal(8_000_000_003, second.HouseInformation.Price);
        Assert.All(account.Houses, house => Assert.Null(Assert.IsType<HouseInformationCaseHouseAccount>(house.InformationCase).Value.HouseInformation.GuildInformation));
        Assert.Equal(3, first.HouseInformation.RoomsCount);
        Assert.Equal(2, first.HouseInformation.ChestsCount);
        Assert.Contains(339, first.HouseInformation.SkillsId);
        Assert.NotNull(first.HouseInformation.AccountTag);
        Insert(1, Account, guild.Id, 9, 73, "9999");
        Assert.Null(first.HouseInformation.Price);
        Assert.False(first.HouseInformation.IsAdminLocked);
        Assert.Equal(217, Assert.IsType<HouseInformationCaseHouseGuild>(shared.Houses[0].InformationCase).Value.GuildShareParams);
        Assert.Same(wrong, SessionContext.Actual);
        Assert.Equal(0, pipe.Session.UnoCadaVez.CurrentCount);
        Assert.All(codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(0, codec.Decodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_account_always_sends_and_empty_guild_only_sends_for_members(bool guilded)
    {
        if (guilded) GuildStore.Create(Character, "EmptyGuild", 1, 2, 3, 4);
        await using var pipe = await ClientPipe.OpenAsync(Account, Character, Street);
        await HouseHandler.SendAccountHousesAsync(pipe.Session);
        await HouseHandler.SendGuildHousesAsync(pipe.Session);
        Assert.Empty(await pipe.Next("jaa"));
        if (guilded) Assert.Empty(await pipe.Next("jjs"));
        Assert.False(pipe.FromServer.DataAvailable);
    }

    [Fact]
    public async Task Populated_account_and_guild_lists_use_the_real_framed_codec()
    {
        var guild = GuildStore.Create(Character, "FramedGuild", 1, 2, 3, 4);
        Insert(3, Account, guild.Id, 505, 8_000_000_003, "1234");
        await using var pipe = await ClientPipe.OpenAsync(Account, Character, Street);
        await HouseHandler.SendAccountHousesAsync(pipe.Session);
        await HouseHandler.SendGuildHousesAsync(pipe.Session);
        var codec = new GameCodec();
        var account = Assert.Single(Assert.IsType<HouseAccountEvent>(codec.Decode(await Packet(pipe)).Content).Houses);
        var shared = Assert.Single(Assert.IsType<GuildHousesEvent>(codec.Decode(await Packet(pipe)).Content).Houses);
        Assert.Equal(8_000_000_003, Assert.IsType<HouseInformationCaseHouseAccount>(account.InformationCase).Value.HouseInformation.Price);
        var guildHouse = Assert.IsType<HouseInformationCaseHouseGuild>(shared.InformationCase).Value;
        Assert.Equal(505, guildHouse.GuildShareParams);
        Assert.Equal(3, guildHouse.HouseInformation.InstanceId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Codec_and_transport_failures_propagate_without_releasing_the_callers_turn(bool guildList, bool codecFailure)
    {
        GuildStore.Create(Character, "FailureGuild", 1, 2, 3, 4);
        var failure = new IOException("house delivery failed");
        var codec = new RecordingCodec { Failure = codecFailure ? failure : null };
        var session = new GameSession(codec, _ => throw failure);
        session.BindAccount(Account, 1);
        session.State.CharacterId = Character;
        using var turn = await session.EnterTurnAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => guildList
            ? HouseHandler.SendGuildHousesAsync(session) : HouseHandler.SendAccountHousesAsync(session)));
        Assert.Single(codec.Frames);
        Assert.Equal(0, session.UnoCadaVez.CurrentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sale_list_keeps_burst_order_and_its_codec_failure_stops_trailing_notifications(bool fail)
    {
        Insert(1, Account, 0, 0, 73, "");
        var codec = new RecordingCodec { Failure = fail ? new IOException("list encoding failed") : null };
        await using var pipe = await ClientPipe.OpenAsync(Account, Character, Street, codec: codec);
        pipe.Session.State.HouseDialog = new HouseHandler.Dialog
        {
            Kind = HouseHandler.DialogKind.Sale, DoorMapId = Street, DoorElementId = Door, Instance = 1, OpenedOn = Street,
        };
        using var wrong = SessionContext.Push(GameSession.SinSocket());
        using var turn = await pipe.Session.EnterTurnAsync();
        var request = ConnectionProtocol.Push("jan", Pb.New().Var(2, 1).Build());
        if (fail) await Assert.ThrowsAsync<IOException>(() => HouseHandler.SellAsync(pipe.Session, request));
        else Assert.True(await HouseHandler.SellAsync(pipe.Session, request));
        await pipe.Next("iwm");
        await pipe.Next("izz");
        var list = Assert.IsType<HouseAccountEvent>(Assert.Single(codec.Frames).Content);
        Assert.Null(Assert.IsType<HouseInformationCaseHouseAccount>(Assert.Single(list.Houses).InformationCase).Value.HouseInformation.Price);
        if (!fail)
        {
            Assert.Equal(new byte[] { 0x71, 1 }, await Packet(pipe));
            await pipe.Next("izu");
            await pipe.Next("izz");
            await pipe.Next("kld");
        }
        Assert.False(pipe.FromServer.DataAvailable);
        Assert.Equal(0, HouseStore.Of(Street, Door, 1).Price);
        Assert.Equal(0, pipe.Session.UnoCadaVez.CurrentCount);
    }

    [Fact]
    public async Task Guild_sharing_refreshes_the_owners_list_after_guild_and_rights_notices()
    {
        var guild = GuildStore.Create(Character, "ShareGuild", 1, 2, 3, 4);
        Insert(1, Account, 0, 0, 0, "1234");
        var codec = new RecordingCodec();
        await using var pipe = await ClientPipe.OpenAsync(Account, Character, Street, codec: codec);
        var request = ConnectionProtocol.Push("jao", Pb.New().Var(1, 1).Var(2, Houses.HouseIdOf(Street, Door)).Var(3, 1).Var(4, 505).Build());
        using var wrong = SessionContext.Push(GameSession.SinSocket());
        using var turn = await pipe.Session.EnterTurnAsync();
        Assert.True(await HouseHandler.ShareWithGuildAsync(pipe.Session, request));
        await pipe.Next("jjp");
        await pipe.Next("jak");
        Assert.Equal(new byte[] { 0x71, 1 }, await Packet(pipe));
        var list = Assert.IsType<HouseAccountEvent>(Assert.Single(codec.Frames).Content);
        var house = Assert.IsType<HouseInformationCaseHouseAccount>(Assert.Single(list.Houses).InformationCase).Value;
        Assert.Null(house.HouseInformation.GuildInformation);
        Assert.True(house.HouseInformation.IsAdminLocked);
        Assert.Equal(guild.Id, HouseStore.Of(Street, Door, 1).GuildId);
        Assert.Equal(505, HouseStore.Of(Street, Door, 1).GuildRights);
        Assert.False(pipe.FromServer.DataAvailable);
    }

    [Fact]
    public async Task Guild_lookup_failure_retains_omission_policy()
    {
        GuildStore.ConnectionStringOverride = "Data Source=/does-not-exist/jondo-house-lists.db";
        var codec = new RecordingCodec();
        var session = new GameSession(codec, _ => Task.CompletedTask);
        session.State.CharacterId = Character;
        await HouseHandler.SendGuildHousesAsync(session);
        Assert.Empty(codec.Frames);
    }

    private static void Insert(int instance, long account, long guild, int rights, long price, string code)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"INSERT OR REPLACE INTO Houses (MapId, ElementId, Instance, AccountId, Price, AccessCode, GuildId, GuildRights) VALUES ({Street}, {Door}, $i, $a, $p, $c, $g, $r);";
        command.Parameters.AddWithValue("$i", instance);
        command.Parameters.AddWithValue("$a", account);
        command.Parameters.AddWithValue("$g", guild);
        command.Parameters.AddWithValue("$r", rights);
        command.Parameters.AddWithValue("$p", price);
        command.Parameters.AddWithValue("$c", code);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open() { var db = new SqliteConnection(DatabaseManager.WorldConnectionString); db.Open(); return db; }
    private static void Clear(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"DELETE FROM Houses WHERE MapId = {Street} AND ElementId = {Door};";
        command.ExecuteNonQuery();
    }
    private static async Task<byte[]> Packet(ClientPipe pipe)
        => (await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5)))!;

    private sealed class RecordingCodec : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal IOException? Failure;
        internal int Decodes;
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); if (Failure != null) throw Failure; return [0x71, checked((byte)Frames.Count)]; }
        public GameFrame Decode(ReadOnlySpan<byte> packet) { Decodes++; throw new InvalidOperationException("House lists must not reparse packets."); }
    }
}
