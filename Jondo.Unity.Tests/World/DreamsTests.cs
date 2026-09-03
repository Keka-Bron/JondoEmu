using System;
using System.Collections.Generic;
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
    /// Contadas las nueve capturas que traen un iyj, siempre son CINCO filas: una sala de entrada,
    /// tres filas de entre dos y cuatro salas, y una sala final. El total va de nueve a once.
    ///
    ///   1 2 2 4 1   1 2 3 2 1   1 3 3 3 1   1 2 3 2 1   1 3 3 3 1
    ///   1 2 3 3 1   1 2 2 3 1   1 3 2 2 1   1 2 3 4 1
    ///
    /// El de Pesadilla II, que es el que se usa aquí de patrón, reparte 1 3 3 3 1:
    ///
    ///   0 -> 1,2,3   1 -> 4   2 -> 5,6   3 -> 6   4 -> 7   5 -> 8   6 -> 9   7..9 -> 10
    ///
    /// Y no es un árbol: a la 6 se llega desde la 2 y desde la 3, que es lo que hace que elegir
    /// camino signifique algo.
    /// </remarks>
    [Collection("MapManager")]
    public class DreamsTests
    {
        private static Dreams.Sueno Uno(int nivel = 200, int dificultad = 9)
        {
            // Las salas necesitan los elementos del mapa para tener puertas, y el orden en que
            // xUnit corre las pruebas no está garantizado.
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            return Dreams.Crear(1, "Prueba", nivel, dificultad, 100, 200);
        }

        [Fact]
        public void Once_salas_en_cinco_filas()
        {
            var s = Uno();

            var porFila = s.Salas.GroupBy(x => x.Fila).OrderBy(g => g.Key).Select(g => g.Count()).ToList();

            Assert.Equal(5, porFila.Count);
            Assert.Equal(1, porFila[0]);
            Assert.Equal(1, porFila[4]);
            Assert.All(porFila.Skip(1).Take(3), n => Assert.InRange(n, 2, 4));
            Assert.InRange(s.Salas.Count, 9, 11);
        }

        [Fact]
        public void El_grafo_es_el_de_la_captura()
        {
            var s = Uno();

            int ultima = s.Salas.Max(x => x.Id);
            int ultimaFila = s.Salas.Max(x => x.Fila);

            // Toda sala que no sea la última abre camino, y siempre a la fila de abajo. Un solo
            // callejón sin salida en medio deja el sueño sin terminar y no da ningún error.
            foreach (var sala in s.Salas)
            {
                if (sala.Id == ultima)
                {
                    Assert.Empty(sala.Salidas);
                    continue;
                }

                Assert.NotEmpty(sala.Salidas);
                foreach (int destino in sala.Salidas)
                {
                    Assert.Equal(sala.Fila + 1, s.Buscar(destino)!.Fila);
                }
            }

            // Y a todas se llega desde algún sitio, menos a la entrada.
            foreach (var sala in s.Salas)
            {
                if (sala.Id == 0) continue;
                Assert.Contains(s.Salas, x => x.Salidas.Contains(sala.Id));
            }

            // La última la ofrece toda la fila de encima, como en las nueve capturas.
            foreach (var sala in s.Salas.Where(x => x.Fila == ultimaFila - 1))
            {
                Assert.Contains(ultima, sala.Salidas);
            }
        }

        [Fact]
        public void A_la_sala_de_en_medio_se_llega_por_dos_caminos()
        {
            // Lo que distingue un rombo de un árbol, y está medido: en Pesadilla II a la sala 6 la
            // ofrecen la 2 y la 3. Aquí se comprueba que ALGUNA sala tenga dos padres, que es la
            // propiedad de la que depende que elegir camino signifique algo.
            var s = Uno();

            int conDosPadres = s.Salas.Count(
                sala => s.Salas.Count(x => x.Salidas.Contains(sala.Id)) > 1);

            Assert.True(conDosPadres > 0, "ninguna sala se alcanza por dos caminos: es un árbol");
        }

        [Fact]
        public void Ninguna_sala_ofrece_mas_salidas_que_puertas_tiene()
        {
            // Los mapas de la subárea 904 traen exactamente tres elementos interactivos. Una
            // cuarta salida sería una sala que se dibuja en el mapa del sueño y a la que no hay
            // manera de entrar: el fallo callado de siempre.
            for (int intento = 0; intento < 50; intento++)
            {
                Dreams.OlvidarTodo();
                var s = Dreams.Crear(intento + 1, "Prueba", 200, 5, 100, 200);

                foreach (var sala in s.Salas)
                {
                    Assert.True(sala.Salidas.Count <= 3,
                                $"la sala {sala.Id} ofrece {sala.Salidas.Count} salidas y sólo hay 3 puertas");
                }
            }
        }

        [Fact]
        public void La_entrada_y_el_final_no_llevan_grupo()
        {
            // En la captura la entrada viaja con un solo campo y la última sin el f9 del grupo.
            var s = Uno();

            Assert.Equal(0, s.Buscar(0)!.Grupo);
            Assert.Equal(0, s.Salas[s.Salas.Count - 1].Grupo);
        }

        [Fact]
        public void Las_salas_de_en_medio_traen_grupo_y_modificacion()
        {
            var s = Uno();

            int ultimaFila = s.Salas.Max(x => x.Fila);

            foreach (var sala in s.Salas.Where(x => x.Fila != 0 && x.Fila != ultimaFila))
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
            // una sola, y una de las dos ramas quedaría inalcanzable. Ya no se inventa un número:
            // son los elementos del propio mapa de la sala, que en la subárea 904 son tres.
            Interactives.Initialize();
            var s = Uno();

            foreach (var sala in s.Salas)
            {
                Assert.NotEqual(0, sala.MapaDeLaSala);

                var puertas = new List<int>();
                for (int cual = 0; cual < sala.Salidas.Count; cual++)
                {
                    int puerta = Dreams.PuertaDe(sala, cual);
                    Assert.NotEqual(0, puerta);
                    puertas.Add(puerta);
                }

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
            string crudo = System.Text.Encoding.ASCII.GetString(iyj);

            foreach (var sala in s.Salas)
            {
                Assert.Contains(sala.Id.ToString(), crudo);
            }

            Assert.True(iyj.Length > 100, "el mapa ha salido demasiado corto para nueve salas");
        }

        [Fact]
        public void Los_once_ixf_de_las_capturas_son_diez_comienzos_y_una_continuacion()
        {
            // Censadas las 613 capturas del árbol entero: hay once ixf y nada más. Diez llevan la
            // dificultad —una por peldaño, del 1 al 10— y el que queda es «12020801».
            //
            // Ese último se leyó como «descartar» durante un tiempo, por el nombre del fichero en
            // el que aparecía. Los bytes que le siguen dicen lo contrario: un izg y un jru a una
            // sala, o sea que el jugador ENTRA. Descartar no tiene mensaje: se descarta empezando
            // otro. Esta prueba está aquí para que nadie vuelva a leerlo por el nombre.
            var comienzos = new List<byte[]>();
            for (int dificultad = 1; dificultad <= Dreams.MaximaDificultad; dificultad++)
            {
                comienzos.Add(new byte[] { 0x0a, 0x04, 0x18, (byte)dificultad, 0x20, 0x01 });
            }

            Assert.Equal(10, comienzos.Count);
            Assert.Equal("0a0418012001", Convert.ToHexString(comienzos[0]).ToLowerInvariant());
            Assert.Equal("0a04180a2001", Convert.ToHexString(comienzos[9]).ToLowerInvariant());

            // Y el de continuar, que es f2 { f1: 1 } y no lleva dificultad ninguna.
            byte[] continuar = new byte[] { 0x12, 0x02, 0x08, 0x01 };
            var campos = ProtoMessage.Parse(continuar).Fields;
            var f2 = Assert.Single(campos);
            Assert.Equal(2, f2.FieldNumber);
        }

        [Fact]
        public void Cada_sala_de_pelea_sabe_contra_quien_se_pelea()
        {
            // No basta con el nivel del grupo: para plantarlo en la sala hacen falta los monstruos
            // que lo componen. Los grupos del mundo son mezclados —cinco especies distintas en el
            // primero de la base— y plantar cinco copias del primero cambiaría la pelea sin que
            // se notase en ningún sitio.
            var s = Uno();
            int ultimaFila = s.Salas.Max(x => x.Fila);

            // Las de Favor no pelean: en ellas está el Rey Gob y se pasa hablando.
            foreach (var sala in s.Salas.Where(x => x.Fila != 0 && x.Fila != ultimaFila && !x.EsFavor))
            {
                Assert.NotEmpty(sala.Miembros);
                Assert.All(sala.Miembros, m => Assert.True(m.Monstruo > 0));
            }

            // Y la entrada y la última no pelean.
            Assert.Empty(s.Buscar(0)!.Miembros);
            Assert.Empty(s.Salas[s.Salas.Count - 1].Miembros);
        }

        [Fact]
        public void Cada_sala_tiene_su_propio_mapa_de_la_zona_de_los_suenos()
        {
            // La entrada es siempre la misma y las demás no se repiten: dos salas en el mismo mapa
            // compartirían puertas, y el camino dejaría de significar nada.
            var s = Uno();

            Assert.Equal(Dreams.MapaDeEntrada, s.Buscar(0)!.MapaDeLaSala);

            var mapas = s.Salas.Select(x => x.MapaDeLaSala).ToList();
            Assert.DoesNotContain(0L, mapas);
            Assert.Equal(mapas.Count, mapas.Distinct().Count());
        }

        [Fact]
        public void El_estado_lista_las_TRES_puertas_y_repite_el_grafo()
        {
            // Medido en el izg de Pesadilla II: las tres puertas de la sala, con la que no lleva a
            // ninguna parte incluida, y el grafo entero otra vez en el f16.
            var s = Uno();
            byte[] izg = DreamProtocol.BuildDreamState(s);
            var campos = ProtoMessage.Parse(izg).Fields;

            int puertas = campos.Count(f => f.FieldNumber == 4);
            Assert.Equal(3, puertas);

            // El grafo va en el f16, y no es pequeño: sin él el cliente se queda dentro de la sala
            // sin mapa del sueño, sin lista de bonos y sin bestiario.
            var grafo = campos.FirstOrDefault(f => f.FieldNumber == 16);
            Assert.NotNull(grafo);
            Assert.NotNull(grafo!.BytesValue);
            Assert.True(grafo.BytesValue!.Length > 50,
                        "el f16 del estado ha salido demasiado corto para llevar el grafo");

            // Y el nivel, que va en el f20.
            var nivel = campos.FirstOrDefault(f => f.FieldNumber == 20);
            Assert.NotNull(nivel);
            Assert.Equal(200, (int)nivel!.VarIntValue);
        }

        [Theory]
        [InlineData(1, 50)]
        [InlineData(2, 75)]
        [InlineData(3, 100)]
        [InlineData(4, 120)]
        [InlineData(5, 140)]
        [InlineData(6, 160)]
        [InlineData(7, 190)]
        [InlineData(8, 220)]
        [InlineData(9, 250)]
        [InlineData(10, 300)]
        public void Cada_dificultad_reparte_los_puntos_medidos(int dificultad, int puntos)
        {
            // El f22 de los 39 izg de las capturas: una dificultad, un valor, sin discrepancias.
            // Sin esto el sueño empieza con cero puntos, y con cero puntos el cliente no pinta la
            // ventanita de los bonos, los puntos y la tormenta: no hay nada que enseñar.
            Assert.Equal(puntos, Dreams.PuntosDeSalida(dificultad));

            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var s = Dreams.Crear(1, "Prueba", 200, dificultad, 100, 200);

            Assert.Equal(puntos, s.PuntosDeSalida);
            Assert.Equal(puntos, s.Puntos);
        }

        [Fact]
        public void Limpiar_una_sala_sube_los_puntos_de_ahora_y_no_la_dotacion()
        {
            // Medido en Pesadilla III: los dos valen 300 en la sala 0, y en la 2 el f8 va por 315
            // con el f22 todavía en 300. O sea que el f22 es la dotación y el f8 el total.
            var s = Uno(dificultad: 10);
            int dotacion = s.PuntosDeSalida;

            s.Puntos += 15;

            Assert.Equal(300, dotacion);
            Assert.Equal(315, s.Puntos);
            Assert.Equal(300, s.PuntosDeSalida);
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
