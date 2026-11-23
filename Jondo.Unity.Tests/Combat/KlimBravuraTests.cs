using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Klim's Bravura (23893) holds "792 on A under EON218": an enemy put in Pacifista (218, by
    /// his 3486) casts its grade 2 at himself, a 141 on 'C', and dies. Only the bearer's own
    /// attitudes answered a state change, so the watch on his enemies never went off.
    /// </summary>
    [Collection("MapManager")]
    public class KlimBravuraTests
    {
        private const int Bravura = 23893;
        private const int Pacifista = 3486;

        private static Fighter Player(int id) => new()
        {
            Id = id, Name = "P", CellId = 250, Level = 200, MaxHP = 5000, CurrentHP = 5000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
        };

        [Fact]
        public async Task An_enemy_put_in_Pacifista_dies()
        {
            if (!SpellEffects.De(Bravura, 1).Any(row => row.EffectId == 792)) return;   // no spell data
            var player = Player(9_970_901);
            var klim = new Fighter
            {
                Id = -1, Name = "Klim", IsMonster = true, CellId = 300, Level = 200, MaxHP = 20000,
                CurrentHP = 20000, MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = 3384,
            };
            var fight = new FightInstance(3384, 990_003_384);
            fight.AddPlayer(player);
            fight.AddMonster(klim);
            player.CellId = 250; klim.CellId = 300;
            Assert.Equal(1, ExpeditionModifiers.Give(fight, new ExpeditionModifiers.Modifier(203, 3384, Bravura, "bravura"),
                                                     new[] { 3384 }));
            fight.StartFight();
            await FightHandler.AplicarEfectosAsync(null, fight, klim, Bravura, 1, klim,
                                                   EffectEngine.AlLanzar, klim.CellId, armar: false);
            Assert.True(player.IsAlive);

            // Pacifista ("950 218 on L,M,e152") takes those not yet marked with 152.
            await FightHandler.AplicarEfectosAsync(null, fight, klim, Pacifista, 1, klim,
                                                   EffectEngine.AlLanzar, klim.CellId);
            Assert.True(player.Buffs.TieneEstado(218));
            Assert.False(player.IsAlive);
            Assert.True(klim.IsAlive);
        }

        [Fact]
        public async Task Another_state_sets_nothing_off()
        {
            if (!SpellEffects.De(Bravura, 1).Any(row => row.EffectId == 792)) return;   // no spell data
            var player = Player(9_970_902);
            var klim = new Fighter
            {
                Id = -1, Name = "Klim", IsMonster = true, CellId = 300, Level = 200, MaxHP = 20000,
                CurrentHP = 20000, MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = 3384,
            };
            var fight = new FightInstance(3384, 990_003_385);
            fight.AddPlayer(player);
            fight.AddMonster(klim);
            klim.Buffs.PonerActitud(Bravura);
            fight.StartFight();
            var friend = Player(9_970_903);
            friend.CellId = 260;
            fight.AddPlayer(friend);
            await FightHandler.DispararAsync(null, fight, friend, EffectEngine.AlPonerseElEstado(152));
            Assert.True(friend.IsAlive);
            Assert.True(player.IsAlive);
        }
    }
}
