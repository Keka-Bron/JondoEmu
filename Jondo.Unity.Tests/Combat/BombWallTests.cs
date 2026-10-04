using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// El muro de bombas: quien lo sostiene, qué casillas coge y cómo se le cuenta al cliente.
    /// </summary>
    public class BombWallTests
    {
        private static Fighter Tymador(long id = 10) => new()
        {
            Id = id, TeamId = 0, CellId = 300, MaxHP = 500, CurrentHP = 500,
        };

        private static Fighter Bomba(long id, Fighter dueno, int cell, int template = 3112) => new()
        {
            Id = id, TeamId = dueno.TeamId, CellId = cell, MaxHP = 90, CurrentHP = 90,
            IsMonster = true, MonsterId = template, GradeIndex = 3, Invocador = dueno.Id,
            SummonCost = 0, JuegaTurno = false,
        };

        /// <summary>Una casilla a N pasos de otra, en línea recta por el eje que se pida.</summary>
        private static int EnLinea(int desde, int pasos, bool porY)
        {
            var (x, y) = MapGeometry.CellToPoint(desde);
            return porY ? MapGeometry.PointToCell(x, y + pasos)
                        : MapGeometry.PointToCell(x + pasos, y);
        }

        [Fact]
        public void Dos_bombas_alineadas_levantan_muro_y_cogen_lo_de_en_medio()
        {
            var tymador = Tymador();
            int a = 270, b = EnLinea(a, 4, porY: true);
            var bombas = new[] { Bomba(-2, tymador, a), Bomba(-3, tymador, b) };

            var muros = BombWalls.Of(bombas.Append(tymador), tymador);

            var muro = Assert.Single(muros);
            Assert.Equal(2, muro.Bombs.Count);
            Assert.Equal(3, muro.Cells.Count);          // cuatro de separación, tres en medio
            Assert.DoesNotContain(a, muro.Cells);       // las suyas no son del muro
            Assert.DoesNotContain(b, muro.Cells);
        }

        /// <summary>
        /// The widest wall of the captures: bombs on 144 and 245, six cells apart, raise all six
        /// (frame 4213 of "explobomba-tornabomba-bomba de agua-...").
        /// </summary>
        [Fact]
        public void Six_cells_between_the_bombs_is_still_a_wall()
        {
            var tymador = Tymador();
            var bombas = new[] { Bomba(-9, tymador, 245, 3113), Bomba(-10, tymador, 144, 3113) };

            var muro = Assert.Single(BombWalls.Of(bombas.Append(tymador), tymador));

            Assert.Equal(new[] { 158, 173, 187, 202, 216, 231 }, muro.Cells.OrderBy(c => c));
        }

        /// <summary>
        /// And seven is not: frame 8192 of the same capture, a bomb on 129 next to one on 245,
        /// and the server puts back every wall but that one.
        /// </summary>
        [Fact]
        public void Seven_cells_between_them_is_not()
        {
            var tymador = Tymador();
            var bombas = new[] { Bomba(-6, tymador, 245, 3113), Bomba(-7, tymador, 129, 3113) };

            Assert.Empty(BombWalls.Of(bombas.Append(tymador), tymador));
        }

        [Fact]
        public void Mas_lejos_del_tope_no_hay_muro()
        {
            var tymador = Tymador();
            int a = 270, b = EnLinea(a, BombWalls.MaxGap + 1, porY: true);
            var bombas = new[] { Bomba(-2, tymador, a), Bomba(-3, tymador, b) };

            Assert.Empty(BombWalls.Of(bombas.Append(tymador), tymador));
        }

        [Fact]
        public void Pegadas_tampoco()
        {
            var tymador = Tymador();
            int a = 270, b = EnLinea(a, 1, porY: true);
            var bombas = new[] { Bomba(-2, tymador, a), Bomba(-3, tymador, b) };

            Assert.Empty(BombWalls.Of(bombas.Append(tymador), tymador));
        }

        [Fact]
        public void En_diagonal_no_hay_muro()
        {
            var tymador = Tymador();
            var (x, y) = MapGeometry.CellToPoint(270);
            int b = MapGeometry.PointToCell(x + 2, y + 2);
            var bombas = new[] { Bomba(-2, tymador, 270), Bomba(-3, tymador, b) };

            Assert.Empty(BombWalls.Of(bombas.Append(tymador), tymador));
        }

        [Fact]
        public void Bombas_de_distinto_tipo_no_se_juntan()
        {
            var tymador = Tymador();
            int a = 270, b = EnLinea(a, 3, porY: true);
            var bombas = new[]
            {
                Bomba(-2, tymador, a),
                Bomba(-3, tymador, b, template: 3113),
            };

            Assert.Empty(BombWalls.Of(bombas.Append(tymador), tymador));
        }

        [Fact]
        public void Un_muro_no_pasa_de_tres_bombas()
        {
            var tymador = Tymador();
            var celdas = Enumerable.Range(0, 4).Select(i => EnLinea(200, i * 2, porY: true)).ToArray();
            var bombas = celdas.Select((c, i) => Bomba(-2 - i, tymador, c)).ToArray();

            var muros = BombWalls.Of(bombas.Append(tymador), tymador);

            Assert.All(muros, m => Assert.True(m.Bombs.Count <= BombWalls.MaxBombs));
            Assert.Equal(4, muros.Sum(m => m.Bombs.Count) - (muros.Count - 1));
        }

        [Fact]
        public void Las_bombas_de_otro_tymador_no_cuentan()
        {
            var uno = Tymador(10);
            var otro = Tymador(11);
            int a = 270, b = EnLinea(a, 3, porY: true);
            var bombas = new[] { Bomba(-2, uno, a), Bomba(-3, otro, b) };

            Assert.Empty(BombWalls.Of(bombas.Append(uno).Append(otro), uno));
            Assert.Empty(BombWalls.Of(bombas.Append(uno).Append(otro), otro));
        }

        [Fact]
        public void Una_bomba_muerta_se_lleva_su_muro()
        {
            var tymador = Tymador();
            int a = 270, b = EnLinea(a, 3, porY: true);
            var viva = Bomba(-2, tymador, a);
            var muerta = Bomba(-3, tymador, b);
            var todos = new[] { viva, muerta, tymador };

            Assert.Single(BombWalls.Of(todos, tymador));
            muerta.CurrentHP = 0;
            Assert.Empty(BombWalls.Of(todos, tymador));
        }

        [Fact]
        public void El_paquete_del_glifo_es_el_medido_en_la_captura()
        {
            // f3=dueño f14=401 f32{f1{f1{f2=color f3=casilla} f4=id f5=huella f6=grado
            //                        f9=hechizo f10=casilla f11=1 f12=dueño}}
            byte[] paquete = FightProtocol.BuildGlyph(
                owner: 53721497699, glyphId: 1, cell: 260, spell: 13458, grade: 3,
                size: 2, colour: FightProtocol.GlyphRed);

            // Lo que de verdad importa: los números que lleva dentro.
            var campos = ProtoMessage.Parse(paquete).Fields;
            Assert.Contains(campos, f => f.FieldNumber == 3 && f.VarIntValue == 53721497699);
            Assert.Contains(campos, f => f.FieldNumber == 14 && f.VarIntValue == 401);
            Assert.Contains(campos, f => f.FieldNumber == 32);
        }

        [Fact]
        public void Quitar_un_glifo_son_quince_bytes_y_su_numero()
        {
            byte[] paquete = FightProtocol.BuildGlyphGone(53721497699, 2);

            var campos = ProtoMessage.Parse(paquete).Fields;
            Assert.Contains(campos, f => f.FieldNumber == 3 && f.VarIntValue == 53721497699);
            Assert.Contains(campos, f => f.FieldNumber == 14 && f.VarIntValue == 310);

            var detalle = Assert.Single(campos, f => f.FieldNumber == 22);
            var dentro = ProtoMessage.Parse(detalle.BytesValue).Fields;
            Assert.Equal(2, Assert.Single(dentro, f => f.FieldNumber == 1).VarIntValue);
        }

        /// <summary>
        /// Empezar el turno encima del muro, byte a byte contra el frame 4272 de
        /// «explobomba-tornabomba-bomba de agua-en glifo-en objetivo-dejando que crezcan-
        /// explotandolas.pcapng»: el glifo 6 del tymador salta sobre el -1 en la casilla 274.
        /// </summary>
        [Fact]
        public void Empezar_el_turno_en_el_muro_se_avisa_con_el_307()
        {
            byte[] paquete = FightProtocol.BuildGlyphTriggered(
                owner: 53721497699, glyphId: 6, cell: 274, victim: -1, walkedIn: false);

            Assert.Equal("18e380b490c8014a1008920210ffffffffffffffffff01200670b302",
                         Hex(paquete));
        }

        /// <summary>
        /// Y entrar en él, contra el frame 110 de «glifo de bombas sismobomba.pcapng»: el glifo 1
        /// en la casilla 289 le salta al -4. Mismo cuerpo, el 306 en vez del 307.
        /// </summary>
        [Fact]
        public void Entrar_en_el_muro_se_avisa_con_el_306()
        {
            byte[] paquete = FightProtocol.BuildGlyphTriggered(
                owner: 53721497699, glyphId: 1, cell: 289, victim: -4, walkedIn: true);

            Assert.Equal("18e380b490c8014a1008a10210fcffffffffffffffff01200170b202",
                         Hex(paquete));
        }

        /// <summary>
        /// A bomb wall hits its own Rogue. Only Kabum spares him.
        /// </summary>
        /// <remarks>
        /// The class sheet, twice: "Una entidad que se desplace en el muro o entre en el sufrira
        /// danos" -- an entity, not an enemy -- and "Los hechizos Kabum e Impostura permiten
        /// aplicar el estado Kabum al lanzador y a sus aliados, que los protege de los danos de
        /// las explosiones y de los muros".
        ///
        /// The 92 comes from effect 950 of spells 13450 and 13489, not from anybody guess.
        /// </remarks>
        [Fact]
        public void A_bomb_wall_hits_the_rogue_who_raised_it()
        {
            var fight = new FightInstance(1, 1);
            var tymador = Tymador();
            fight.AddPlayer(tymador);
            var muro = Muro(tymador.Id, 260);

            Assert.True(FightHandler.GlyphCatches(fight, muro, tymador, byDisplacement: false));

            tymador.Buffs.PonerEstado(BombWalls.KabumState);
            Assert.False(FightHandler.GlyphCatches(fight, muro, tymador, byDisplacement: false));
        }

        /// <summary>
        /// And a glyph that is not a wall still spares its own: a Feca does not burn himself on
        /// his own walking over it.
        /// </summary>
        [Fact]
        public void Any_other_glyph_still_spares_its_owner()
        {
            var fight = new FightInstance(1, 1);
            var feca = Tymador(20);
            var otro = Tymador(21);
            fight.AddPlayer(feca);
            fight.AddPlayer(otro);

            var glifo = new Glifo(feca.Id, new[] { 260 }, GlifoDeFeca, 3, FightProtocol.GlyphRed,
                                  caducaEnRonda: 0, mascara: "", cuando: Disparo.AlPisarYAlEmpezar);

            Assert.False(FightHandler.GlyphCatches(fight, glifo, feca, byDisplacement: false));
            Assert.True(FightHandler.GlyphCatches(fight, glifo, otro, byDisplacement: false));
        }

        /// <summary>
        /// A wall never catches the bombs of the Rogue holding it up.
        /// </summary>
        /// <remarks>
        /// Three of the eight displacements onto a wall cell in the captures are his own bombs --
        /// frames 10552 and 10645 of "explobomba-tornabomba-...-explotandolas" and 337 of
        /// "tymador-cruce" -- and none of the three sets the wall off. Which is the only way
        /// Imantacion can work: it drags the bombs along the very line they hold.
        /// </remarks>
        [Fact]
        public void A_wall_never_catches_the_bombs_that_hold_it()
        {
            var fight = new FightInstance(1, 1);
            var tymador = Tymador();
            var bomba = Bomba(-5, tymador, 260);
            fight.AddPlayer(tymador);
            fight.AddPlayer(bomba);

            var muro = Muro(tymador.Id, bomba.CellId);
            Assert.False(FightHandler.GlyphCatches(fight, muro, bomba, byDisplacement: true));
            Assert.False(FightHandler.GlyphCatches(fight, muro, bomba, byDisplacement: false));
        }

        /// <summary>
        /// Once a turn when pushed in; every single cell when walked.
        /// </summary>
        /// <remarks>
        /// "Si esta entidad ya ha sufrido los efectos del muro durante su turno y vuelven a
        /// mandarla a el, su desplazamiento no se detendra ni sufrira los danos. No obstante,
        /// caminar en el muro no se ve afectado por este limite."
        ///
        /// The measured half is the push: of the five displacements onto a wall cell that are not
        /// the Rogue own bombs, four go off and the fifth -- frame 8281, -1 pulled from 231 to
        /// 216 -- is the only one already caught earlier in that same turn, at frame 8250.
        /// </remarks>
        [Fact]
        public void The_once_a_turn_limit_is_only_for_being_pushed()
        {
            var fight = new FightInstance(1, 1);
            var tymador = Tymador();
            var bicho = new Fighter { Id = -1, TeamId = 1, CellId = 260, MaxHP = 500, CurrentHP = 500 };
            fight.AddPlayer(tymador);
            fight.AddPlayer(bicho);
            var muro = Muro(tymador.Id, 260);

            Assert.True(FightHandler.GlyphCatches(fight, muro, bicho, byDisplacement: true));

            fight.WallHitThisTurn.Add(bicho.Id);
            Assert.False(FightHandler.GlyphCatches(fight, muro, bicho, byDisplacement: true));
            Assert.True(FightHandler.GlyphCatches(fight, muro, bicho, byDisplacement: false));
        }

        /// <summary>
        /// A displacement steps ONTO the wall and stops there.
        /// </summary>
        /// <remarks>
        /// "Desplazar una entidad a un muro detendra su desplazamiento y le infligira danos."
        /// Measured: frame 8282 pulls -3 from 274 to 260, a wall cell, and 8283 is the wall going
        /// off on it at 260 -- into the wall, not short of it.
        ///
        /// And no collision damage: BlockedCells is what feeds it, and the caller leaves it alone
        /// when the stop was a wall.
        /// </remarks>
        [Fact]
        public void A_wall_stops_a_push_on_the_cell_itself()
        {
            var pisables = new HashSet<int>();
            for (int c = 0; c < 560; c++) pisables.Add(c);

            // Sin muro: recorre las cuatro casillas que le piden.
            var libre = Zone.Push(centro: 300, deQuienLanza: 300, aQuien: 301, casillas: 4,
                                  pisables: pisables, ocupadas: new HashSet<int>());
            Assert.Equal(Zone.PushStop.None, libre.Stop);

            // Con muro en la segunda: entra en ella y ahi se queda.
            int segunda = Zone.Push(centro: 300, deQuienLanza: 300, aQuien: 301, casillas: 2,
                                    pisables: pisables, ocupadas: new HashSet<int>()).ToCell;
            var frenado = Zone.Push(centro: 300, deQuienLanza: 300, aQuien: 301, casillas: 4,
                                    pisables: pisables, ocupadas: new HashSet<int>(),
                                    paran: new HashSet<int> { segunda });

            Assert.Equal(Zone.PushStop.Wall, frenado.Stop);
            Assert.Equal(segunda, frenado.ToCell);
        }

        private static Glifo Muro(long dueno, int casilla)
            => new(dueno, new[] { casilla }, BombWalls.WallSpell[3112], 3, FightProtocol.GlyphRed,
                   caducaEnRonda: 0, mascara: "", cuando: Disparo.AlPisarYAlEmpezar);

        /// <summary>Any spell that is not one of the four wall ones.</summary>
        private const int GlifoDeFeca = 13525;

        private static string Hex(byte[] bytes)
            => string.Concat(bytes.Select(b => b.ToString("x2")));
    }
}
