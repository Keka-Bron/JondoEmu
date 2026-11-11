using System.Collections.Concurrent;
using System.Reflection;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat;

public sealed class GameEntryFightMapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_roleplay_map_requests_are_consumed_without_spending_fight_preparation_or_closing_the_session(bool contextReady)
    {
        const long characterId = 990_000_851, fightId = 7_100_851, arena = 990_100_851;
        var active = (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
            .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var fight = new FightInstance(fightId, arena);
        fight.Azul.Add(new Fighter { Id = characterId, CurrentHP = 100 });
        await using var pipe = await ClientPipe.OpenAsync(8_900_000_851, characterId, arena);
        pipe.Session.State.IsInFight = true;
        pipe.Session.State.FightId = fightId;
        Assert.True(active.TryAdd(fightId, fight));
        try
        {
            GameRequest request = contextReady
                ? new ContextReadyRequest(arena - 1)
                : new MapInformationRequest((int)arena - 1);
            var entry = new GameEntryService(pipe.Session, new GameEntryBackend());
            using var turn = await pipe.Session.EnterTurnAsync();

            Assert.Equal(GameEntryResult.Handled, await entry.HandleAsync(new(request, -1)));

            Assert.False(fight.HasPrepared(characterId));
            using (SessionContext.Push(pipe.Session)) Assert.Same(fight, FightHandler.PendingPreparation());
            Assert.True(pipe.Session.IsInWorld);
            Assert.Equal(arena, pipe.Session.MapId);
            await pipe.Quiet();
        }
        finally { active.TryRemove(fightId, out _); }
    }
}
