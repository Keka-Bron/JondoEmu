using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Capitan Meno's summoning runes (6804): grades 1-5 lay the runes and their "792 grade 6"
    /// sets them all off at once (2023), each bringing its monsters. Kept on the ground as
    /// stepping runes, each monster that came out of one fired it again at its own turn start,
    /// and the room filled with some thirty monsters by the second round.
    /// </summary>
    [Collection("MapManager")]
    public class MenoRuneTests
    {
        private const int SummoningRunes = 6804;
        private const int Rune = 2022;

        private static Fighter Player(int cell) => new()
        {
            Id = 9_970_401, Name = "P", CellId = cell, Level = 200, MaxHP = 5000, CurrentHP = 5000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
        };

        private static Fighter Meno(int cell) => new()
        {
            Id = -1, Name = "Meno", IsMonster = true, CellId = cell, Level = 200, MaxHP = 30000,
            CurrentHP = 30000, MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = 4460,
        };

        [Fact]
        public async Task His_runes_go_off_once_as_he_lays_them_and_are_spent()
        {
            var player = Player(250);
            var boss = Meno(100);
            var fight = new FightInstance(4460, 990_004_460);
            fight.AddPlayer(player);
            player.CellId = 250;
            fight.AddMonster(boss);
            boss.CellId = 100;

            await FightHandler.AplicarEfectosAsync(null, fight, boss, SummoningRunes, 1, null,
                                                   EffectEngine.AlLanzar, 300);
            int afterTheCast = fight.Todos.Count(f => f.IsAlive);
            Assert.Contains(fight.Todos, f => f.IsAlive && f.EsInvocado);
            Assert.DoesNotContain(fight.Glifos, g => g.Tipo == Rune && !g.Gastado);

            // Whoever starts a turn where a rune was sets nothing off.
            foreach (var monster in fight.Todos.Where(f => f.IsAlive && f.EsInvocado).ToList())
                await FightHandler.DispararLosGlifosAsync(null, fight, monster, alPisar: false);
            player.CellId = 300;
            await FightHandler.DispararLosGlifosAsync(null, fight, player, alPisar: false);
            await FightHandler.DispararLosGlifosAsync(null, fight, player, alPisar: true);
            Assert.Equal(afterTheCast, fight.Todos.Count(f => f.IsAlive));
        }

        [Fact]
        public async Task He_heals_by_what_a_player_suffers_and_by_nothing_off_an_invulnerable_one()
        {
            const int Behaviour = 6793, Invulnerable = 56;
            var player = Player(250);
            var boss = Meno(100);
            boss.CurrentHP = 20000;
            var monster = new Fighter
            {
                Id = -2, Name = "Gambaf", IsMonster = true, CellId = 251, Level = 200, MaxHP = 4000,
                CurrentHP = 4000, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, MonsterId = 4455,
            };
            var fight = new FightInstance(4460, 990_004_461);
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            fight.AddMonster(monster);
            player.CellId = 250; boss.CellId = 100; monster.CellId = 251;

            // His behaviour at the start of the fight: "792 6795 on L under D", whose 2020 heals
            // him by all that player suffers.
            await FightHandler.AplicarEfectosAsync(null, fight, boss, Behaviour, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);

            player.Buffs.PonerEstado(Invulnerable);
            await FightHandler.UnGolpeAsync(null, fight, monster, 6773, blow, 4, player, 95, 0, false);
            Assert.Equal(5000, player.CurrentHP);
            Assert.Equal(20000, boss.CurrentHP);

            player.Buffs.QuitarEstado(Invulnerable);
            await FightHandler.UnGolpeAsync(null, fight, monster, 6773, blow, 4, player, 95, 0, false);
            int suffered = 5000 - player.CurrentHP;
            Assert.True(suffered > 0);
            Assert.Equal(20000 + suffered, boss.CurrentHP);
        }
    }
}
