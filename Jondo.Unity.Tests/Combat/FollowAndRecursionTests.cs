using System;
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
    /// Lazo Espiritual's bond (2184), against the Osamodas capture, and Recursividad's turret
    /// (2017), read off its sheet.
    /// </summary>
    [Collection("MapManager")]
    public class FollowAndRecursionTests
    {
        private const long Osamodas = 53720776803;

        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private static Fighter Character(long id, int team, int cell) => new()
        {
            Id = id, Name = "P" + id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static int Beside(int cell, int dx, int dy)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return MapGeometry.PointToCell(x + dx, y + dy);
        }

        /// <summary>
        /// Frames 2123-2126 and 2138-2141 of "osamodas-todos los hechizos con invocaciones-no
        /// variantes": the summon -12 two cells from its Osamodas steps once into contact, in its
        /// own walk sequence and with its master's facing; and again when he walks one away.
        /// </summary>
        [Fact]
        public async Task The_bond_walks_the_summon_into_contact()
        {
            var fight = new FightInstance(4104, 0, 0);
            var osamodas = Character(Osamodas, 0, 260);
            fight.AddPlayer(osamodas);
            osamodas.CellId = 260;
            var summon = new Fighter { Id = -12, Name = "S", TeamId = 0, CellId = 287, MaxHP = 500, CurrentHP = 500, IsMonster = true, MonsterId = 5153 };
            fight.Invocar(summon, osamodas);
            summon.CellId = 287;
            osamodas.LastFacing = 3;
            await using var wire = await PortalTests.Wire.Open(Osamodas);

            await FightHandler.FollowAsync(fight, summon, osamodas, 2);
            Assert.Equal(273, summon.CellId);
            osamodas.MoverA(246);
            osamodas.LastFacing = 7;
            await FightHandler.FollowAsync(fight, summon, osamodas, 2);
            Assert.Equal(260, summon.CellId);

            var frames = await wire.Drain();
            Assert.Equal(new[] { "jto", "jsj", "jwi", "jto", "jsj", "jwi" }, frames.Select(f => f.Op));
            Assert.Equal(Hex("08f4ffffffffffffffff011004"), frames[0].Payload);                  // 2124
            Assert.Equal(Hex("0a049f029102100328f4ffffffffffffffff01"), frames[1].Payload);      // 2125
            Assert.Equal(Hex("0a0491028402100728f4ffffffffffffffff01"), frames[4].Payload);      // 2140
        }

        /// <summary>
        /// The bond's hooks on the Osamodas, "1160 31194-6 under M|TP|EON8|EOFF8|PT|TE": the jxm of
        /// its EON8 and EOFF8 carry "EON" and "EOFF", the state left behind -- frames 2114-2115,
        /// byte for byte, and the 575 others of the captures.
        /// </summary>
        [Fact]
        public void A_state_trigger_goes_out_bare()
        {
            Assert.Equal(Hex("0a4f0a4308daf30110e3808890c8011892012001320210093a03454f4e409ee21850e707621610ffffffffffffffffff0118ffffffffffffffffff01680670daf301780780010110e3808890c801188809"),
                         Jondo.Unity.Server.Network.FightProtocol.BuildBuff(Osamodas, Osamodas, 146, 1160, 405790, 999, 31194, 6, 31194,
                                                                            "EON8", 9, 2, 7, grado: 1));
            Assert.Equal(Hex("0a500a4408daf30110e3808890c8011893012001320210093a04454f4646409ee21850e707621610ffffffffffffffffff0118ffffffffffffffffff01680670daf301780780010110e3808890c801188809"),
                         Jondo.Unity.Server.Network.FightProtocol.BuildBuff(Osamodas, Osamodas, 147, 1160, 405790, 999, 31194, 6, 31194,
                                                                            "EOFF8", 9, 2, 7, grado: 1));
            Assert.Equal("EK", Jondo.Unity.Server.Network.FightProtocol.WireTrigger("EK:m"));
            Assert.Equal("CMPARR", Jondo.Unity.Server.Network.FightProtocol.WireTrigger("CMPARR"));
        }

        /// <summary>
        /// Lazo Espiritual's grade 2, cast by the Osamodas at his own cell, sends after him the ones
        /// his bond holds (state 6290) and nobody else.
        /// </summary>
        [Fact]
        public void The_bond_names_the_bound_only()
        {
            var fight = new FightInstance(1, 1);
            var osamodas = Character(Osamodas, 0, 260);
            var bound = Character(2, 0, 290);
            var free = Character(3, 0, 330);
            fight.AddPlayer(osamodas); fight.AddPlayer(bound); fight.AddPlayer(free);
            bound.Buffs.PonerEstado(6290);

            var outcomes = EffectEngine.ResolveEffects(fight, osamodas, 31194, 2, osamodas, EffectEngine.AlLanzar, 1,
                                                       SpellEffects.De(31194, 2), aimedCell: 260);

            var follows = Assert.Single(outcomes, o => o.Follows > 0);
            Assert.Same(bound, follows.Sobre);
            Assert.Equal(2, follows.Follows);
        }

        /// <summary>
        /// Recursividad: the enemy pushed next to a turret casts 13881's grade 1, whose 2017 has
        /// that turret cast grade 3 back at him -- he is thrown to the other side of the turret
        /// (1105) and the turret takes the mark that keeps it from doing it twice (2460).
        /// </summary>
        [Fact]
        public void A_turret_in_contact_throws_the_enemy_over()
        {
            var fight = new FightInstance(1, 1);
            var steamer = Character(359450607906, 0, 100);
            var enemy = Character(-1, 1, 300);
            fight.AddPlayer(steamer); fight.AddOpponent(enemy);
            int turretCell = Beside(300, 1, 0);
            var turret = new Fighter { Id = -5, TeamId = 0, CellId = turretCell, MaxHP = 500, CurrentHP = 500, IsMonster = true, MonsterId = 5831 };
            fight.Invocar(turret, steamer);
            turret.CellId = turretCell;

            var outcomes = EffectEngine.Resolver(fight, enemy, 13881, 1, enemy, EffectEngine.AlLanzar, 1, celdaApuntada: 300);

            var thrown = Assert.Single(outcomes, o => o.Efecto.EffectId == 1105);
            Assert.Same(enemy, thrown.Sobre);
            Assert.Equal(MapGeometry.Reflejar(300, turretCell), thrown.CasillaHasta);
            Assert.True(turret.Buffs.TieneEstado(2460));
        }
    }
}
