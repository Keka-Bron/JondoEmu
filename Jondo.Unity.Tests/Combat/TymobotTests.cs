using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Tymobot's four tools, replayed on the geometry of its capture: the bot at 287, its
    /// owner's bomb at 273, and 260 one cell further along the same line.
    /// </summary>
    /// <remarks>
    /// None of it is the bot's own: "hasta la casilla objetivo" is a push or a pull whose length
    /// is worked out from the aimed cell, and carrying and throwing are the Pandawa's Karcham and
    /// Chamrak. Each lives in the engine once.
    /// </remarks>
    public class TymobotTests
    {
        private const int Empujoncito = 13451;
        private const int Aspirador = 13452;
        private const int Pinzas = 30938;
        private const int Tymobot = 3120;
        private const int Explobomba = 3112;

        private static (FightInstance Fight, Fighter Rogue, Fighter Bot, Fighter Bomb) Board()
        {
            var fight = new FightInstance(1, 1);
            var rogue = new Fighter { Id = 10, TeamId = 0, CellId = 301, MaxHP = 2000, CurrentHP = 2000, Level = 200 };
            fight.AddPlayer(rogue);
            var bot = new Fighter
            {
                Id = -15, TeamId = 0, CellId = 287, MaxHP = 100, CurrentHP = 100, IsMonster = true,
                MonsterId = Tymobot, GradeIndex = 3, Level = 3, Invocador = rogue.Id, SummonCost = 0,
            };
            var bomb = new Fighter
            {
                Id = -13, TeamId = 0, CellId = 273, MaxHP = 90, CurrentHP = 90, IsMonster = true,
                MonsterId = Explobomba, GradeIndex = 3, Level = 3, Invocador = rogue.Id, SummonCost = 0,
                JuegaTurno = false,
            };
            fight.Invocar(bot, rogue);
            fight.Invocar(bomb, rogue);
            return (fight, rogue, bot, bomb);
        }

        /// <summary>Aimed at 260 with the bomb at 273 on the way: it ends on 260, and travels as a 5 from 273 to 260.</summary>
        [Fact]
        public void Empujoncito_pushes_the_bomb_on_the_line_to_the_aimed_cell()
        {
            var (fight, _, bot, bomb) = Board();

            var outcomes = EffectEngine.Resolver(fight, bot, Empujoncito, 3, null, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 260);

            Assert.Equal(260, bomb.CellId);
            var moved = Assert.Single(outcomes, o => o.Mueve && o.Sobre == bomb);
            Assert.Equal(273, moved.CasillaDesde);
            Assert.Equal(260, moved.CasillaHasta);
            Assert.Equal(0, moved.CollisionDamage);
        }

        /// <summary>Aimed at 273 with the bomb at 260 past it: it comes back to 273.</summary>
        [Fact]
        public void Aspirador_pulls_the_bomb_past_the_line_to_the_aimed_cell()
        {
            var (fight, _, bot, bomb) = Board();
            bomb.CellId = 260;

            EffectEngine.Resolver(fight, bot, Aspirador, 3, null, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: 273);

            Assert.Equal(273, bomb.CellId);
        }

        /// <summary>Nobody on the line, nothing moves; and the effect never touches whoever stands ON the aimed cell.</summary>
        [Fact]
        public void The_line_effects_need_somebody_on_the_line()
        {
            var (fight, _, bot, bomb) = Board();
            bomb.CellId = 260;

            var outcomes = EffectEngine.Resolver(fight, bot, Empujoncito, 3, null, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 260);

            Assert.Equal(260, bomb.CellId);
            Assert.DoesNotContain(outcomes, o => o.Mueve);
        }

        /// <summary>Pinzas at 273 picks the bomb up; Pinzas at 260 sets it down there. States 3 and 8 in between.</summary>
        [Fact]
        public void Pinzas_carries_and_then_throws()
        {
            var (fight, _, bot, bomb) = Board();

            var lifted = EffectEngine.Resolver(fight, bot, Pinzas, 1, bomb, EffectEngine.AlLanzar,
                                               fight.RoundNumber, celdaApuntada: 273);

            Assert.Equal(bot.Id, bomb.CarriedBy);
            Assert.Equal(bomb.Id, bot.Carrying);
            Assert.Equal(bot.CellId, bomb.CellId);
            Assert.True(bot.Buffs.TieneEstado(EffectSupport.CarryingState));
            Assert.True(bomb.Buffs.TieneEstado(EffectSupport.CarriedState));
            var carry = Assert.Single(lifted, o => o.Carga);
            Assert.Equal(273, carry.CasillaDesde);

            // A second pick-up while carrying does nothing: "*e3" keeps the caster out.
            var again = EffectEngine.Resolver(fight, bot, Pinzas, 1, bomb, EffectEngine.AlLanzar,
                                              fight.RoundNumber, celdaApuntada: 287);
            Assert.DoesNotContain(again, o => o.Carga);

            var thrown = EffectEngine.Resolver(fight, bot, Pinzas, 1, null, EffectEngine.AlLanzar,
                                               fight.RoundNumber, celdaApuntada: 260);

            Assert.Equal(260, bomb.CellId);
            Assert.Equal(0, bomb.CarriedBy);
            Assert.Equal(0, bot.Carrying);
            Assert.False(bot.Buffs.TieneEstado(EffectSupport.CarryingState));
            Assert.False(bomb.Buffs.TieneEstado(EffectSupport.CarriedState));
            var landing = Assert.Single(thrown, o => o.Lanza);
            Assert.Equal(260, landing.CasillaHasta);
        }

        /// <summary>The two frames of the capture, byte for byte.</summary>
        [Fact]
        public void Carry_and_throw_travel_as_50_and_51()
        {
            Assert.Equal("18f0ffffffffffffffff01703292010e08910218f3ffffffffffffffff01",
                         Hex(FightProtocol.BuildCarry(-16, 273, -13)));
            Assert.Equal("18f0ffffffffffffffff017033da010e08f3ffffffffffffffff01108402",
                         Hex(FightProtocol.BuildThrow(-16, -13, 260)));
        }

        /// <summary>The segment 'l': from the caster towards the aimed cell, the caster left out at min 1.</summary>
        [Fact]
        public void The_segment_runs_from_the_caster_to_the_aimed_cell()
        {
            var cells = Zone.Casillas(Zone.Segmento, 1, 287, 260, minimo: 63);

            Assert.Equal(new[] { 273, 260 }, cells);
            Assert.Contains(287, Zone.Casillas(Zone.Segmento, 0, 287, 260, minimo: 63));
        }

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));
    }
}
