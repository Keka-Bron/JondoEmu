using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// An item's attitude chains grades that wait on their own triggers. They answer once cast,
    /// as the hook they would leave on the bearer, and however deep the chain goes.
    /// </summary>
    public class ItemAttitudeChainTests
    {
        /// <summary>"El portador se vuelve invisible durante 1 turno al principio de su primer turno"; a blow ends it.</summary>
        private const int Prismausente = 14940;

        /// <summary>Above half his life: +7% final damage, and 10% of his life lost at the start of each turn.</summary>
        private const int BurladorDeLaMuerte = 11359;

        private const int Invisible = 150;

        /// <summary>Heals 60% of the damage taken in the first turn, 30% in the second, 10% in the third.</summary>
        private const int Prisanacion = 14926;

        private static Fighter Character(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 900, CurrentHP = 900, Level = 50,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494,
        };

        private static int Beside(int cell, int dx, int dy)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return MapGeometry.PointToCell(x + dx, y + dy);
        }

        /// <summary>Worn into the fight: the attitude, and its rows of the start.</summary>
        private static async Task<(FightInstance Fight, Fighter Bearer)> Wearing(int spell)
        {
            var fight = new FightInstance(1, 1);
            var bearer = Character(1, 0, 300);
            fight.AddPlayer(bearer);
            fight.AddOpponent(Monster(-1, Beside(300, 2, 0)));
            bearer.Buffs.PonerActitud(spell, givenInRound: 1);
            await FightHandler.AplicarEfectosAsync(null, fight, bearer, spell, 1, bearer, EffectEngine.AlLanzar,
                                                   bearer.CellId, armar: false);
            return (fight, bearer);
        }

        [Fact]
        public async Task A_blow_before_his_first_turn_leaves_Prismausente_on()
        {
            var (fight, bearer) = await Wearing(Prismausente);

            await FightHandler.DispararAsync(null, fight, bearer, "D");

            Assert.Contains(Prismausente, bearer.Buffs.Actitudes);
            await FightHandler.DispararAsync(null, fight, bearer, "TB");
            Assert.Contains(bearer.Buffs.Puestos, b => b.EffectId == Invisible && b.HechizoOrigen == Prismausente);
        }

        [Fact]
        public async Task A_blow_takes_Prismausente_invisibility_away()
        {
            var (fight, bearer) = await Wearing(Prismausente);
            await FightHandler.DispararAsync(null, fight, bearer, "TB");
            Assert.Contains(bearer.Buffs.Puestos, b => b.EffectId == Invisible);

            await FightHandler.DispararAsync(null, fight, bearer, "D");

            Assert.DoesNotContain(bearer.Buffs.Puestos, b => b.EffectId == Invisible);
        }

        [Fact]
        public async Task Prismausente_is_over_at_his_second_turn()
        {
            var (fight, bearer) = await Wearing(Prismausente);
            await FightHandler.DispararAsync(null, fight, bearer, "TB");
            Assert.Contains(Prismausente, bearer.Buffs.Actitudes);

            fight.StartFight();
            for (int turns = 0; turns < 4 && fight.RoundNumber < 2; turns++) fight.NextTurn();
            await FightHandler.DispararAsync(null, fight, bearer, "TB");

            Assert.Equal(2, fight.RoundNumber);
            Assert.DoesNotContain(Prismausente, bearer.Buffs.Actitudes);
        }

        [Fact]
        public void A_marker_ends_the_stretch_before_it()
        {
            var effects = SpellEffects.De(Prisanacion, 1);
            var casts = effects.Where(e => e.EffectId == EffectEngine.EfectoQueLanzaHechizo).ToList();

            Assert.Equal(3, casts.Count);
            Assert.Equal(1, EffectEngine.EndOfItsStretch(effects, casts[0]));
            Assert.Equal(2, EffectEngine.EndOfItsStretch(effects, casts[1]));
            Assert.Null(EffectEngine.EndOfItsStretch(effects, casts[2]));
        }

        [Fact]
        public async Task Above_half_his_life_the_Burlador_takes_life_at_the_turn_start()
        {
            var (fight, bearer) = await Wearing(BurladorDeLaMuerte);
            int before = bearer.CurrentHP;

            await FightHandler.DispararAsync(null, fight, bearer, "TB");

            Assert.True(bearer.CurrentHP < before, $"life {before} -> {bearer.CurrentHP}");
        }

        [Fact]
        public async Task Under_half_his_life_the_Burlador_takes_none()
        {
            var (fight, bearer) = await Wearing(BurladorDeLaMuerte);
            bearer.CurrentHP = 800;
            await FightHandler.DispararAsync(null, fight, bearer, "D");
            int before = bearer.CurrentHP;

            await FightHandler.DispararAsync(null, fight, bearer, "TB");

            Assert.Equal(before, bearer.CurrentHP);
        }
    }
}
