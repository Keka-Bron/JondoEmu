using Jondo.Unity.Server.Bots;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.UI;

/// <summary>
/// What the dashboard's Players page does to the world: the same as the administrators' «.kick»
/// and «.announce» commands, from the server's own window.
/// </summary>
internal static class ServerControl
{
    /// <summary>One character in the world, as the Players page lists it.</summary>
    internal sealed record Player(Guid Session, string Name, int Level, long MapId, bool Fighting, bool Simulated);

    /// <summary>The characters in the world now.</summary>
    internal static IReadOnlyList<Player> Players()
        => SessionRegistry.InWorld()
            .Select(s => new Player(s.Id, s.State.CharacterName ?? "", s.State.CharacterLevel, s.MapId,
                                    s.State.IsInFight, SimulatedPlayers.IsSimulated(s.AccountId)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Closes that character's connection, as a second login of its account would: the session's
    /// own way out saves the character. False when the session is gone or has no socket (a
    /// simulated player).
    /// </summary>
    internal static bool Kick(Guid session)
    {
        var player = SessionRegistry.All().FirstOrDefault(s => s.Id == session);
        if (player?.Stream == null) return false;
        try { player.Stream.Dispose(); } catch { }
        Console.WriteLine($"[Server] {player.State.CharacterName} was kicked from the server window.");
        return true;
    }

    /// <summary>A line in every player's chat, in each one's language; how many it reached.</summary>
    internal static async Task<int> Announce(string text)
    {
        int told = await CommandHandler.AnnounceToAllAsync("announce.line", text);
        Console.WriteLine($"[Server] Announcement from the server window to {told} player(s): {text}");
        return told;
    }
}
