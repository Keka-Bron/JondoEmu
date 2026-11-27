using System.Net.Sockets;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Mods;

internal static class PlayerPolicies
{
    internal static bool BlocksAutomaticEncounter(long characterId, AutomaticEncounter encounter)
        => ServerModHooks.All<IPlayerActivityPolicy>().Any(policy => policy.BlocksAutomaticEncounter(characterId, encounter));

    internal static async Task<bool> BlocksActivityAsync(NetworkStream? stream, PlayerActivity activity)
    {
        foreach (var policy in ServerModHooks.All<IPlayerActivityPolicy>())
            if (await policy.BlocksActivityAsync(SessionContext.State.CharacterId, activity, SessionContext.State.Language,
                stream == null ? null : text => WriteNoticeAsync(stream, text)))
                return true;
        return false;
    }

    internal static async Task<bool> BlocksDuelAsync(NetworkStream stream, long characterId, long otherId)
    {
        foreach (var policy in ServerModHooks.All<IPlayerActivityPolicy>())
            if (await policy.BlocksDuelAsync(characterId, otherId, SessionContext.State.Language,
                text => WriteNoticeAsync(stream, text)))
                return true;
        return false;
    }

    internal static void AlignmentChanged(long characterId)
    {
        foreach (var hook in ServerModHooks.All<IPvpEvents>()) hook.AlignmentChanged(characterId);
    }

    internal static void CharacterDeleted(long characterId)
    {
        foreach (var hook in ServerModHooks.All<IPvpEvents>()) hook.CharacterDeleted(characterId);
    }

    internal static async Task AggressionEndedAsync(FightInstance fight, bool attackersWon)
    {
        var hooks = ServerModHooks.All<IPvpEvents>();
        if (hooks.Count == 0) return;
        var winners = People(attackersWon ? fight.Azul : fight.Rojo);
        var losers = People(attackersWon ? fight.Rojo : fight.Azul);
        foreach (var hook in hooks) await hook.AggressionEndedAsync(winners, losers);
    }

    private static PvpCombatant[] People(IEnumerable<Fighter> side)
        => side.Where(f => !f.IsMonster && !f.EsInvocado && f.Id > 0)
            .Select(f => new PvpCombatant(f.Id, f.Level)).ToArray();

    private static async Task WriteNoticeAsync(NetworkStream stream, string text)
        => await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
            ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text)));
}
