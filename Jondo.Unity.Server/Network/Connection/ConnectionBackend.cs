using System.Globalization;
using Jondo.Unity.Protocol.Connection;
using ConnectionServer = Jondo.Unity.Protocol.Connection.Server;

namespace Jondo.Unity.Server.Network.Connection;

/// <summary>Projects Jondo's account data and launch registry into the stable protocol.</summary>
internal sealed class ConnectionBackend : IConnectionBackend
{
    public long ResolveAccount(string token) => ClientLaunchRegistry.ResolveToken(token);

    public IdentificationSuccess Identify(long accountId)
    {
        var account = DatabaseManager.GetAccountById(accountId);
        var servers = DatabaseManager.GetServers();
        var characters = DatabaseManager.GetCharactersByAccountId(accountId);
        Console.WriteLine($"[Connection Server] Account {accountId}: {servers.Count} server(s), {characters.Count} character(s).");
        return new IdentificationSuccess(accountId, account?.Nickname ?? "Jondo", GameServerProxy.BuildAccountTag(accountId),
            ConnectionAccountProjection.ServerList(servers, characters, DateTimeOffset.Now),
            Subscription.EndDateFor(accountId), new AccountRights(false, false, false, false), null, null, null);
    }

    public bool IsServerJoinable(int serverId) => DatabaseManager.IsServerJoinable(serverId);

    public SelectServerSuccess IssueTicket(long accountId, int serverId)
    {
        // A connection correlation ID is not a language code. The launch owns the
        // locale; a connection without a launch uses the session registry's usual Spanish default.
        string language = ClientLaunchRegistry.TryGetByAccount(accountId, out var launch) && launch != null
            ? launch.Language : "es";
        var ticket = SessionRegistry.Issue(accountId, serverId, language);
        Console.WriteLine($"[Connection Server] Account {accountId} is joining server {serverId}; reconnect to port {Program.gamePort}.");
        return new SelectServerSuccess(ticket.Value, "127.0.0.1", new[] { Program.gamePort, Program.gamePort });
    }
}

internal static class ConnectionAccountProjection
{
    internal const string DateFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    internal static ServerList ServerList(IReadOnlyList<DatabaseManager.DbServer> servers,
        IReadOnlyList<DatabaseManager.DbCharacter> characters, DateTimeOffset now)
        => new(servers.Select(server => new ServerInformation(
            // Preserve the existing connection reply's default Online/Accessible values.
            // DB Status is an HTTP enum (3=online), not the connection enum. HTTP owns
            // the selection-screen availability, and Joinable is enforced before ticket issuance.
            new ConnectionServer(server.Id, ConnectionServerStatus.Online, Category(server.Type)),
            ServerAccessibility.Accessible,
            characters.Where(c => c.ServerId == server.Id).Select(c => new CharacterSummary(
                c.Name, Breed(c.Breed), Gender(c.Sex), c.Level,
                string.IsNullOrEmpty(c.LastConnection) ? now.ToString(DateFormat, CultureInfo.InvariantCulture) : c.LastConnection))
                .ToArray())).ToArray(),
            Enumerable.Range(0, 7).Select(category => new ServerSlots(Category(category), ConnectionService.MaxCharactersPerServer)).ToArray());

    private static ServerCategory Category(int value) => value switch
    {
        0 => ServerCategory.MonoAccount, 1 => ServerCategory.Classic, 2 => ServerCategory.PioneerMonoAccount,
        3 => ServerCategory.PioneerClassic, 4 => ServerCategory.Tournament, 5 => ServerCategory.Epic,
        6 => ServerCategory.Temporis, _ => throw new InvalidOperationException($"Unknown database server category: {value}.")
    };

    private static CharacterGender Gender(int value) => value switch
    {
        0 => CharacterGender.Male, 1 => CharacterGender.Female,
        _ => throw new InvalidOperationException($"Unknown database character gender: {value}.")
    };

    // There is no breed 19: the Forgelance is 20, and the client reads wire value 18 back as 20
    // (gr's constructor; anything past 18 is no breed at all).
    private static CharacterBreed Breed(int value) => value switch
    {
        1 => CharacterBreed.Feca, 2 => CharacterBreed.Osamodas, 3 => CharacterBreed.Enutrof,
        4 => CharacterBreed.Sram, 5 => CharacterBreed.Xelor, 6 => CharacterBreed.Ecaflip,
        7 => CharacterBreed.Eniripsa, 8 => CharacterBreed.Iop, 9 => CharacterBreed.Cra,
        10 => CharacterBreed.Sadida, 11 => CharacterBreed.Sacrier, 12 => CharacterBreed.Pandawa,
        13 => CharacterBreed.Rogue, 14 => CharacterBreed.Masqueraider, 15 => CharacterBreed.Foggernaut,
        16 => CharacterBreed.Eliotrope, 17 => CharacterBreed.Huppermage, 18 => CharacterBreed.Ouginak,
        20 => CharacterBreed.Forgelance,
        _ => throw new InvalidOperationException($"Unknown database character breed: {value}.")
    };
}
