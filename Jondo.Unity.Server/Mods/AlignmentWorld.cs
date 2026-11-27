using System.Data.Common;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Mods;

internal sealed class AlignmentWorld : IAlignmentWorld, IWorldStorage, ISimulatedPlayerWorld
{
    public SelectedCharacter? FindSelectedCharacter(long accountId)
        => ControlApi.InTheWorld(accountId) is { State: var state }
            ? new(state.CharacterId, state.CharacterName, state.CharacterLevel)
            : null;

    public WorldCharacter? FindCharacter(long characterId)
        => DatabaseManager.GetCharacterById(characterId) is { } row
            ? new(characterId, row.Name, row.Level, row.Breed)
            : null;

    public IEnumerable<PvpCharacter> FindPvpCharacters(int side, int minimumLevel)
    {
        using var connection = OpenWorldDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.CharacterId, c.Name, c.Level, c.Breed, COALESCE(c.LastConnection, '') " +
                              "FROM CharacterAlignments a JOIN Characters c ON c.Id = a.CharacterId " +
                              "WHERE a.Side = $side AND a.Pvp = 1 AND c.Level >= $minimum;";
        command.Parameters.AddWithValue("$side", side);
        command.Parameters.AddWithValue("$minimum", minimumLevel);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return new(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)),
                reader.GetString(4));
    }

    public CharacterPresence? FindPresence(long characterId)
        => SessionRegistry.FindByCharacter(characterId) is { State: var state } session
            ? new(state.CharacterName, state.Language, session.IsInWorld, state.IsInFight, state.MapId)
            : null;

    public WorldLocation Locate(long mapId)
    {
        var map = MapManager.GetMapInfo(mapId);
        return new(map != null,
            map != null && MapManager.OnTheWorldMap(mapId) ? (map.PosX, map.PosY) : null);
    }

    public PvpAlignment AlignmentOf(long characterId)
    {
        var alignment = Alignments.Of(characterId);
        return new(alignment.Side, alignment.Side switch
        {
            Alignments.Bonta => Alignments.Brakmar,
            Alignments.Brakmar => Alignments.Bonta,
            _ => null,
        }, alignment.Pvp, alignment.HonourGrade, alignment.Honour, alignment.Dishonour);
    }

    DbConnection IWorldStorage.OpenWorldDatabase() => OpenWorldDatabase();

    private static SqliteConnection OpenWorldDatabase()
    {
        var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void AddHonour(long characterId, int amount) => Alignments.AddHonour(characterId, amount);

    public async Task RaisePvpWingsAsync(long characterId)
    {
        Alignments.SetPvp(characterId, true);
        if (SessionRegistry.FindByCharacter(characterId) is not { } session) return;
        using var context = SessionContext.Push(session);
        await CharacteristicsMessages.Live.SendAsync(session);
        await SlotLookHandler.RedrawAsync(session.Stream);
    }

    public async Task LeaveArenaQueueAsync(long characterId)
    {
        if (!KoliseoQueue.Waits(characterId) || SessionRegistry.FindByCharacter(characterId) is not { Stream: { } stream } session)
            return;
        using var context = SessionContext.Push(session);
        await KoliseoHandler.LeaveQueueAsync(stream);
    }

    public Task SendNoticeAsync(long characterId, string text)
        => SendAsync(characterId, ConnectionProtocol.BuildNotice(text));

    public Task SendSystemMessageAsync(long characterId, int message, params string[] arguments)
        => SendAsync(characterId, ConnectionProtocol.BuildSystemMessage(message, arguments));

    private static async Task SendAsync(long characterId, byte[] payload)
    {
        var session = SessionRegistry.FindByCharacter(characterId);
        if (session == null || !session.IsInWorld) return;
        try { await session.SendAsync(ConnectionProtocol.Push(Op.Lqn, payload)); }
        catch (Exception ex) { Program.LogDebug($"[Character notices] {characterId} could not be told: {ex.Message}"); }
    }

    public IDisposable QuietForBots(long first, long second)
        => ConsoleLogBuffer.QuietWhile(Bots.SimulatedPlayers.OnlyBots(new[] { first, second }));
}
