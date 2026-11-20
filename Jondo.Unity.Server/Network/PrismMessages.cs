using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal sealed class PrismMessages(ItemEffectMessages effects, Func<int, IReadOnlyList<Equipment.ItemEffect>> crystalEffects)
{
    internal static PrismMessages Live { get; } = new(ItemEffectMessages.FromCatalogue, gid =>
        DatabaseManager.TryGetItemTemplateEffects(gid, out string json) ? Equipment.ParseEffects(json) : []);

    internal PrismListEvent Snapshot(GameSession session)
    {
        long mine = AllianceStore.AllianceOf(session.CharacterId)?.Id ?? 0;
        var result = new List<PrismLocalizedInformation>();
        foreach (var prism in PrismStore.All())
        {
            var alliance = AllianceStore.ById(prism.AllianceId);
            if (alliance == null) continue;
            result.Add(Localized(prism, alliance, own: alliance.Id == mine));
        }
        return new(result.ToArray());
    }

    internal PrismLocalizedInformation Localized(PrismStore.Prism prism, AllianceStore.Alliance alliance, bool own)
    {
        var map = MapManager.GetMapInfo(prism.MapId);
        // The legacy duplicate alliance UUID is indeterminate; the nested alliance retains its UID.
        return new(prism.MapId, new(map?.PosX ?? 0, map?.PosY ?? 0), prism.SubAreaId, State(prism, alliance, own));
    }

    internal PrismInformation State(PrismStore.Prism prism, AllianceStore.Alliance alliance, bool own = false)
        => new(own ? Math.Max(0, PrismProtocol.MaxCrystals - prism.CrystalsUsed) : null,
            prism.State == PrismStore.Normal || prism.NextChangeUtcMs <= 0 ? prism.PlacedUtcMs : prism.NextChangeUtcMs,
            prism.State switch
            {
                PrismStore.Normal => PrismState.Normal,
                PrismStore.Weakened => PrismState.Weakened,
                PrismStore.Vulnerable => PrismState.Vulnerable,
                PrismStore.Pacified => PrismState.Protected,
                _ => throw new InvalidOperationException($"Unsupported prism state {prism.State}."),
            },
            prism.PlacedUtcMs / 1000, MapActorMessages.Alliance(alliance), own ? Crystal(prism) : null);

    private PrismCristal Crystal(PrismStore.Prism prism)
    {
        // Owners require both the inventory wrapper and its item even without a crystal.
        var item = prism.CrystalGid == 0 ? new ObjectItem(0, [], 0, 0, [])
            : new ObjectItem(prism.CrystalGid, crystalEffects(prism.CrystalGid).Select(effects.Effect).OfType<ObjectEffect>().ToArray(),
                1, checked((int)PrismProtocol.CrystalUid(prism)), []);
        return new(new(0, false, item), prism.HasActiveCrystal ? PrismCristalType.Protective : PrismCristalType.NoCristal);
    }
}
