using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// El mapa de un sueño, contra el grafo que trae la captura.
    /// </summary>
    /// <remarks>
    /// El grafo medido en la captura de Paradoja I es un rombo de once salas en cinco filas:
    ///
    ///   0 -> 1,2   1 -> 3,4   2 -> 4,5   3 -> 6,7   4 -> 7,8   5 -> 8,9   6..9 -> 10
    ///
    /// Y no es un árbol: a la sala 4 se llega desde la 1 y desde la 2, que es lo que hace que
    /// elegir camino signifique algo.
    /// </remarks>
    public class DreamsTests
    {
        private static Dreams.Sueno Uno(int nivel = 200, int dificultad = 9)
        {
            Dreams.OlvidarTodo();
            return Dreams.Crear(1, "Prueba", nivel, dificultad, 100, 200);
        }

        [Fact]
        public void Once_salas_en_cinco_filas()
        {
            var s = Uno();

            Assert.Equal(11, s.Salas.Count);
            Assert.Equal(new[] { 1, 2, 3, 4, 1 },
                         s.Salas.GroupBy(x => x.Fila).OrderBy(g => g.Key).Select(g => g.Count()));
        }

        [Fact]
        public void El_grafo_es_el_de_la_captura()
        {
            var s = Uno();

            void Va(int desde, params int[] hasta)
                => Assert.Equal(hasta, s.Buscar(desde)!.Salidas);

            Va(0, 1, 2);
            Va(1, 3, 4);
            Va(2, 4, 5);
            Va(3, 6, 7);
            Va(4, 7, 8);
            Va(5, 8, 9);
            Va(6, 10);
            Va(7, 10);
            Va(8, 10);
            Va(9, 10);

            // La última no lleva a ninguna parte: es el final del sueño.
            Assert.Empty(s.Buscar(10)!.Salidas);
        }

        [Fact]
        public void A_la_sala_de_en_medio_se_llega_por_dos_caminos()
        {
            // Lo que distingue un rombo de un árbol, y está medido: la 4 la ofrecen la 1 y la 2.
            var s = Uno();

            var quienes = s.Salas.Where(x => x.Salidas.Contains(4)).Select(x => x.Id).ToList();
            Assert.Equal(new[] { 1, 2 }, quienes);
        }

        [Fact]
        public void La_entrada_y_el_final_no_llevan_grupo()
        {
            // En la captura la sala «0» viaja con un solo campo y la «10» sin el f9 del grupo.
            var s = Uno();

            Assert.Equal(0, s.Buscar(0)!.Grupo);
            Assert.Equal(0, s.Buscar(10)!.Grupo);
        }

        [Fact]
        public void Las_salas_de_en_medio_traen_grupo_y_modificacion()
        {
            var s = Uno();

            foreach (var sala in s.Salas.Where(x => x.Id != 0 && x.Fila != 4))
            {
                Assert.True(sala.Grupo > 0, $"la sala {sala.Id} se ha quedado sin grupo");
                Assert.True(sala.MapaId > 0, $"la sala {sala.Id} se ha quedado sin mapa");

                // Los cuatro efectos medidos: fuerza, agilidad, vitalidad e inteligencia.
                Assert.Contains(sala.Efecto, new[] { 118, 119, 125, 126 });
                Assert.True(sala.Valor > 0);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(50)]
        [InlineData(200)]
        public void Se_puebla_a_cualquier_nivel(int nivel)
        {
            // La banda de nivel se abre si no hay grupos cerca: hay tramos del mundo sin grupos
            // del nivel exacto, y una sala sin grupo es una sala que no se puede jugar.
            var s = Uno(nivel);

            Assert.All(s.Salas.Where(x => x.Id != 0 && x.Fila != 4),
                       sala => Assert.True(sala.Grupo > 0));
        }

        [Fact]
        public void Las_puertas_de_una_sala_no_se_repiten()
        {
            // El cliente las distingue por su elemento: dos puertas con el mismo número serían
            // una sola, y una de las dos ramas quedaría inalcanzable.
            var s = Uno();

            foreach (var sala in s.Salas)
            {
                var puertas = sala.Salidas
                    .Select(d => DreamHandler.ElementoDePuerta(s.CharacterId, d))
                    .ToList();
                Assert.Equal(puertas.Count, puertas.Distinct().Count());
            }
        }

        [Fact]
        public void El_estado_lleva_la_dificultad_que_se_pidio()
        {
            // El f2 del izg es la dificultad, y en la captura de Pesadilla II vale 9: el mismo
            // número que se mandó en el ixf. Eso es lo que ata los dos mensajes.
            var s = Uno(dificultad: 9);
            byte[] izg = DreamProtocol.BuildDreamState(s);

            var campos = ProtoMessage.Parse(izg).Fields;
            var f2 = campos.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0);

            Assert.NotNull(f2);
            Assert.Equal(9, f2!.VarIntValue);
        }

        [Fact]
        public void El_mapa_del_sueno_nombra_las_salas_como_cadena()
        {
            // No es un capricho: en la captura viajan como «0», «1», «2». Mandarlas como número
            // deja al cliente sin mapa y sin un solo error.
            var s = Uno();
            byte[] iyj = DreamProtocol.BuildDreamMap(s);

            Assert.Contains("10", System.Text.Encoding.ASCII.GetString(iyj));
            Assert.True(iyj.Length > 100, "el mapa ha salido demasiado corto para once salas");
        }

        [Fact]
        public void La_dificultad_se_queda_dentro_de_la_escalera()
        {
            // Diez peldaños: 1..3 Sueño, 4..7 Paradoja, 8..10 Pesadilla.
            Assert.Equal(10, Dreams.MaximaDificultad);

            Dreams.OlvidarTodo();
            Assert.Equal(10, Dreams.Crear(2, "x", 200, 99, 1, 1).Dificultad);
            Dreams.OlvidarTodo();
            Assert.Equal(1, Dreams.Crear(3, "x", 200, 0, 1, 1).Dificultad);
        }
    }
}
