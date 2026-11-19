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
    /// Funoroshi (6249) in the Fábrica de Fuegos Artificiales. His behaviour spell, 16760, arms
    /// "792 16785 under EON1881" on every player: whoever walks into a black glyph and takes
    /// Extinción de los fuegos (1881) loses 3 range and 20 AP and MP dodge while he holds it
    /// (Dofus pour les noobs: «vous perdez 3 PO et 20 esquives PA/PM»).
    /// </summary>
    public class FunoroshiFireTests
    {
        private const int Funoroshi = 6249, Behaviour = 16760, ExtinguisherGift = 16815;
        private const int PutOutTheFires = 1881, LessRange = 16785;
        private const int Fire = 6248, FireBehaviour = 16860, PutOut = 16864, FireIsOut = 1902, Invulnerable = 56;

        private static bool HasData => SpellEffects.De(Behaviour, 1).Any(row => row.DiceNum == LessRange);

        private static (FightInstance Fight, Fighter Boss, Fighter Character) Board()
        {
            var fight = new FightInstance(1, 1);
            var character = new Fighter
            {
                Id = 10, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
            fight.AddPlayer(character);
            var boss = new Fighter
            {
                Id = -1, CellId = 200, MaxHP = 6000, CurrentHP = 6000, Level = 200, IsMonster = true,
                MonsterId = Funoroshi, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
            };
            fight.AddMonster(boss);
            fight.StartFight();
            return (fight, boss, character);
        }

        [Fact]
        public async Task Taking_the_state_of_the_black_glyph_costs_the_player_three_range()
        {
            if (!HasData) return;
            var (fight, boss, character) = Board();
            boss.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);

            // What the black glyph casts on whoever walks in: its 16815, by him, at his cell.
            await FightHandler.AplicarEfectosAsync(null, fight, character, ExtinguisherGift, 1, character,
                                                   EffectEngine.AlLanzar, character.CellId);

            Assert.True(character.Buffs.TieneEstado(PutOutTheFires));
            Assert.Contains(character.Buffs.Puestos, b => b.HechizoOrigen == LessRange && b.Cuanto == -3);
        }

        [Fact]
        public async Task Walking_into_a_black_glyph_costs_the_player_three_range()
        {
            if (!HasData) return;
            var (fight, boss, character) = Board();
            boss.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            var barrel = new Fighter
            {
                Id = -13, CellId = 400, MaxHP = 3100, CurrentHP = 3100, Level = 200, IsMonster = true,
                MonsterId = 6309, MaxAP = 0, CurrentAP = 0, MaxMP = 0, CurrentMP = 0, SummonCost = 1,
            };
            fight.Invocar(barrel, boss);
            var glyph = fight.Poner(new Glifo(barrel.Id, new[] { character.CellId }, 16805, 1, 0, -1, "A",
                                              Jondo.Unity.World.Fights.Disparo.AlPisarYAlEmpezar));
            glyph.Tipo = 1091;

            await FightHandler.DispararLosGlifosAsync(null, fight, character, alPisar: true);

            Assert.True(character.Buffs.TieneEstado(PutOutTheFires));
            Assert.Contains(character.Buffs.Puestos, b => b.HechizoOrigen == LessRange && b.Cuanto == -3);
        }

        /// <summary>
        /// «Pour les rendre vulnérables il faut éteindre tous les feux»: at his turn start the
        /// behaviour gives him 1886 for every fire still lit (16858) and then takes the
        /// invulnerability off his side when he has none (16859). With one fire left he keeps it.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task His_side_loses_its_invulnerability_once_every_fire_is_out(bool everyFire)
        {
            if (!HasData) return;
            var (fight, boss, character) = Board();
            foreach (int cell in new[] { 262, 359 })
            {
                var fire = new Fighter
                {
                    Id = -100 - cell, CellId = cell, MaxHP = 1, CurrentHP = 1, Level = 200, IsMonster = true,
                    MonsterId = Fire, SummonCost = 1, Conducta = (FireBehaviour, 1),
                };
                fight.Invocar(fire, boss);
            }
            boss.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            foreach (var fire in fight.Todos.Where(f => f.MonsterId == Fire).ToList())
                await FightHandler.LanzarLaConductaAsync(null, fight, fire);
            Assert.True(boss.Buffs.TieneEstado(Invulnerable));

            var fires = fight.Todos.Where(f => f.MonsterId == Fire && f.IsAlive).ToList();
            Assert.NotEmpty(fires);
            foreach (var fire in everyFire ? fires : fires.Skip(1))
            {
                // Put out from the cell its glyph stands on, below to its right.
                var (x, y) = MapGeometry.CellToPoint(fire.CellId);
                character.CellId = MapGeometry.PointToCell(x + 1, y);
                await FightHandler.AplicarEfectosAsync(null, fight, character, PutOut, 1, character,
                                                       EffectEngine.AlLanzar, character.CellId);
                Assert.True(fire.Buffs.TieneEstado(FireIsOut));
            }

            await FightHandler.DispararAsync(null, fight, boss, EffectEngine.AlEmpezarElTurno);

            Assert.Equal(!everyFire, boss.Buffs.TieneEstado(Invulnerable));
        }
    }
}
