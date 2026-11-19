using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IWardrobeStateBackend
{
    TitlesAndOrnamentsEvent Owned();
    CosmeticInventoryLoadedEvent Cosmetics(long accountId);
    DbCharacter? Character(long characterId);
    IReadOnlyList<Outfits.Outfit> Outfits(long characterId);
    Task RefreshAsync(GameSession owner, DbCharacter character, bool includeMap);
    OutfitLookUpdatedEvent Look(DbCharacter character);
}

internal sealed class WardrobeStateService(IWardrobeStateBackend backend, WardrobeMessages messages)
{
    internal static WardrobeStateService Live { get; } = new(new LiveBackend(), WardrobeMessages.Live);

    internal async Task SendOwnedAsync(GameSession owner)
    {
        using var context = SessionContext.Push(owner);
        var owned = backend.Owned();
        await owner.SendAsync(owned);
        await owner.SendAsync(backend.Cosmetics(owner.AccountId));
        var character = backend.Character(owner.CharacterId);
        if (character != null)
        {
            await owner.SendAsync(messages.Snapshot(owner, character, backend.Outfits(character.Id)));
            // The aura worn, right after the outfits as in the captures: the client's main aura.
            await owner.SendAsync(InitialPresentationMessages.Aura(Wardrobe.AuraOf(character.Id)));
        }
        await AnnounceAsync(owner);
        Console.WriteLine($"[Appearances] {owned.Titles.Count} titles and {owned.Ornaments.Count} ornaments offered.");
    }

    internal async Task AnnounceAsync(GameSession owner)
    {
        using var context = SessionContext.Push(owner);
        // Indeterminate scalars are omitted under the wardrobe/after-selection policy.
        await owner.SendAsync(new TitleSelectedEvent());
        await owner.SendAsync(new OrnamentSelectedEvent());
        var character = backend.Character(owner.CharacterId);
        if (character == null) return;
        await backend.RefreshAsync(owner, character, includeMap: !owner.State.IsInFight);
        await owner.SendAsync(backend.Look(character));
    }

    private sealed class LiveBackend : IWardrobeStateBackend
    {
        public TitlesAndOrnamentsEvent Owned() => WardrobeMessages.Owned(Titles.All, Titles.AllOrnaments);
        public CosmeticInventoryLoadedEvent Cosmetics(long accountId) => CosmeticCollectionMessages.Live.ForAccount(accountId);
        public DbCharacter? Character(long characterId) => DatabaseManager.GetCharacterById(characterId);
        public IReadOnlyList<Outfits.Outfit> Outfits(long characterId) => Managers.Outfits.Of(characterId);
        public Task RefreshAsync(GameSession owner, DbCharacter character, bool includeMap)
            => MapPresenceService.Live.RefreshAsync(owner, character, includeMap);
        public OutfitLookUpdatedEvent Look(DbCharacter character) => WardrobeMessages.LookChanged(character);
    }
}
