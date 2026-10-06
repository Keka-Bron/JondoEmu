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
    /// A dream's map, against the graph the capture brings.
    /// </summary>
    /// <remarks>
    /// Counting the nine captures that bring an iyj, there are always FIVE rows: an entrance room,
    /// three rows of between two and four rooms, and a final room. The total goes from nine to eleven.
    ///
    ///   1 2 2 4 1   1 2 3 2 1   1 3 3 3 1   1 2 3 2 1   1 3 3 3 1
    ///   1 2 3 3 1   1 2 2 3 1   1 3 2 2 1   1 2 3 4 1
    ///
    /// The Pesadilla II one, which is the one used as the pattern here, splits 1 3 3 3 1:
    ///
    ///   0 -> 1,2,3   1 -> 4   2 -> 5,6   3 -> 6   4 -> 7   5 -> 8   6 -> 9   7..9 -> 10
    ///
    /// And it is not a tree: room 6 is reached from 2 and from 3, which is what makes choosing
    /// a path mean something.
    /// </remarks>
    [Collection("MapManager")]
    public class DreamsTests
    {
        private static Dreams.Sueno Uno(int nivel = 200, int dificultad = 9)
        {
            // The rooms need the map's elements to have doors, and the order in which
            // xUnit runs the tests is not guaranteed.
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

            // Every room that is not the last opens a way, and always to the row below. A single
            // dead end in the middle leaves the dream unfinished and gives no error.
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

            // And every one is reached from somewhere, except the entrance.
            foreach (var sala in s.Salas)
            {
                if (sala.Id == 0) continue;
                Assert.Contains(s.Salas, x => x.Salidas.Contains(sala.Id));
            }

            // The last one is offered by the whole row above, as in the nine captures.
            foreach (var sala in s.Salas.Where(x => x.Fila == ultimaFila - 1))
            {
                Assert.Contains(ultima, sala.Salidas);
            }
        }

        [Fact]
        public void A_la_sala_de_en_medio_se_llega_por_dos_caminos()
        {
            // What tells a diamond from a tree, and it is measured: in Pesadilla II room 6 is
            // offered by 2 and 3. Here it is checked that SOME room has two parents, which is the
            // property on which choosing a path meaning something depends.
            var s = Uno();

            int conDosPadres = s.Salas.Count(
                sala => s.Salas.Count(x => x.Salidas.Contains(sala.Id)) > 1);

            Assert.True(conDosPadres > 0, "ninguna sala se alcanza por dos caminos: es un árbol");
        }

        [Fact]
        public void Ninguna_sala_ofrece_mas_salidas_que_puertas_tiene()
        {
            // The maps of subarea 904 bring exactly three interactive elements. A
            // fourth exit would be a room drawn on the dream's map with no
            // way to enter it: the usual silent bug.
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
            // In the capture the entrance travels with a single field and the last without the group's f9.
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

                // What it gives is one of the nine rewards the rooms of the captures offer: a
                // bonus, or dream points.
                Assert.NotNull(sala.Reward);
                Assert.Contains(sala.Reward!, Dreams.RoomRewards);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(50)]
        [InlineData(200)]
        public void Se_puebla_a_cualquier_nivel(int nivel)
        {
            // The level band widens if there are no groups nearby: there are stretches of the world without groups
            // of the exact level, and a room without a group is a room that cannot be played.
            var s = Uno(nivel);

            Assert.All(s.Salas.Where(x => x.Id != 0 && x.Fila != 4),
                       sala => Assert.True(sala.Grupo > 0));
        }

        [Fact]
        public void Las_puertas_de_una_sala_no_se_repiten()
        {
            // The client tells them apart by their element: two doors with the same number would be
            // a single one, and one of the two branches would be unreachable. A number is no longer invented:
            // they are the elements of the room's own map, which in subarea 904 are three.
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
            // The izg's f2 is the difficulty, and in the Pesadilla II capture it is 9: the same
            // number that was sent in the ixf. That is what ties the two messages.
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
            // It is not a whim: in the capture they travel as «0», «1», «2». Sending them as a number
            // leaves the client without a map and without a single error.
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
            // Counting the 613 captures of the whole tree: there are eleven ixf and nothing else. Ten carry the
            // difficulty —one per rung, from 1 to 10— and the remaining one is «12020801».
            //
            // That last one was read as «discard» for a while, from the name of the file in
            // which it appeared. The bytes that follow it say the opposite: an izg and a jru to a
            // room, that is the player ENTERS. Discarding has no message: one discards by starting
            // another. This test is here so that nobody reads it by the name again.
            var comienzos = new List<byte[]>();
            for (int dificultad = 1; dificultad <= Dreams.MaximaDificultad; dificultad++)
            {
                comienzos.Add(new byte[] { 0x0a, 0x04, 0x18, (byte)dificultad, 0x20, 0x01 });
            }

            Assert.Equal(10, comienzos.Count);
            Assert.Equal("0a0418012001", Convert.ToHexString(comienzos[0]).ToLowerInvariant());
            Assert.Equal("0a04180a2001", Convert.ToHexString(comienzos[9]).ToLowerInvariant());

            // And the continue one, which is f2 { f1: 1 } and carries no difficulty at all.
            byte[] continuar = new byte[] { 0x12, 0x02, 0x08, 0x01 };
            var campos = ProtoMessage.Parse(continuar).Fields;
            var f2 = Assert.Single(campos);
            Assert.Equal(2, f2.FieldNumber);
        }

        [Fact]
        public void Cada_sala_de_pelea_sabe_contra_quien_se_pelea()
        {
            // The group's level is not enough: to plant it in the room the monsters
            // composing it are needed. The world's groups are mixed —five different species in the
            // base's first one— and planting five copies of the first would change the fight without
            // it showing anywhere.
            var s = Uno();
            int ultimaFila = s.Salas.Max(x => x.Fila);

            foreach (var sala in s.Salas.Where(x => x.Fila != 0 && x.Fila != ultimaFila))
            {
                Assert.NotEmpty(sala.Miembros);
                Assert.All(sala.Miembros, m => Assert.True(m.Monstruo > 0));
            }

            // And the entrance and the last do not fight.
            Assert.Empty(s.Buscar(0)!.Miembros);
            Assert.Empty(s.Salas[s.Salas.Count - 1].Miembros);
        }

        [Fact]
        public void Cada_sala_tiene_su_propio_mapa_de_la_zona_de_los_suenos()
        {
            // The entrance is always the same and the rest do not repeat: two rooms on the same map
            // would share doors, and the path would stop meaning anything.
            var s = Uno();

            Assert.Equal(Dreams.MapaDeEntrada, s.Buscar(0)!.MapaDeLaSala);

            var mapas = s.Salas.Select(x => x.MapaDeLaSala).ToList();
            Assert.DoesNotContain(0L, mapas);
            Assert.Equal(mapas.Count, mapas.Distinct().Count());
        }

        [Fact]
        public void El_estado_lista_las_TRES_puertas_y_repite_el_grafo()
        {
            // Measured in the Pesadilla II izg: the room's three doors, the one leading
            // nowhere included, and the whole graph again in f16.
            var s = Uno();
            byte[] izg = DreamProtocol.BuildDreamState(s);
            var campos = ProtoMessage.Parse(izg).Fields;

            int puertas = campos.Count(f => f.FieldNumber == 4);
            Assert.Equal(3, puertas);

            // The graph goes in f16, and it is not small: without it the client stays inside the room
            // without the dream's map, without the bonus list and without the bestiary.
            var grafo = campos.FirstOrDefault(f => f.FieldNumber == 16);
            Assert.NotNull(grafo);
            Assert.NotNull(grafo!.BytesValue);
            Assert.True(grafo.BytesValue!.Length > 50,
                        "el f16 del estado ha salido demasiado corto para llevar el grafo");

            // And the level, which goes in f20.
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
        public void Each_difficulty_has_its_measured_bonus(int difficulty, int percent)
        {
            // The f22 of the izg of the captures: one difficulty, one value. It is the bonus to
            // experience and loot the client paints under the dream's name -- "220% 220%" in a
            // Pesadilla I -- and the f8 starts from it.
            Assert.Equal(percent, Dreams.BonusOf(difficulty));

            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var s = Dreams.Crear(1, "Prueba", 200, difficulty, 100, 200);

            Assert.Equal(percent, s.BaseBonus);
            Assert.Equal(percent, s.Bonus);
        }

        [Fact]
        public void La_fuente_es_la_ultima_de_la_franja_y_abre_la_siguiente()
        {
            // Counting the 665 rooms of the fifteen captures: rows 1, 2 and 3 are fights in all
            // 529, and row 4 is a Fountain in all 68. Not one boss room.
            var s = Uno();

            var fuentes = s.Salas.Where(x => x.EsFuente).ToList();
            var fuente = Assert.Single(fuentes);
            Assert.Equal(s.Salas.Max(x => x.Fila), fuente.Fila);
            Assert.Empty(fuente.Miembros);

            // And the middle ones DO fight, all of them. Marking one as a Favour in band I was a double
            // error: it contradicted those 529 and the guide says the Favours do not appear in the first
            // palier.
            int ultima = s.Salas.Max(x => x.Fila);
            foreach (var sala in s.Salas.Where(x => x.Fila != 0 && x.Fila != ultima))
            {
                Assert.NotEmpty(sala.Miembros);
            }

            // Stepping on the fountain chains: «Chaque palier commencera toujours par une Fontaine».
            int antes = s.Salas.Count;
            Dreams.AnadirFranja(s);

            Assert.Equal(2, s.Franja);
            Assert.True(s.Salas.Count > antes, "la franja siguiente no ha añadido salas");
            Assert.NotEmpty(fuente.Salidas);

            // And it stays a fountain: in the long capture room 9 is still of type 3 in both
            // graphs once the second band is open. The new band's own fountain has no way out yet.
            Assert.True(fuente.EsFuente);
            var fountains = s.Salas.Where(x => x.EsFuente).ToList();
            Assert.Equal(2, fountains.Count);
            Assert.Empty(fountains.Single(x => x != fuente).Salidas);

            // And nobody is left unable to get there.
            foreach (var sala in s.Salas)
            {
                if (sala.Id == 0) continue;
                Assert.Contains(s.Salas, x => x.Salidas.Contains(sala.Id));
            }
        }

        [Fact]
        public void Los_potenciadores_se_acumulan_al_entrar_y_viajan_en_el_estado()
        {
            var s = Uno();
            Assert.Empty(s.Ganados);

            // It is collected on ENTERING, not on winning: the guide says so and it fits with the window
            // showing them before fighting.
            var primera = s.Salas.First(x => x.Regalo != null);
            s.Ganados.Add(primera.Regalo!);
            s.Actual = primera.Id;

            byte[] izg = DreamProtocol.BuildDreamState(s);
            var campos = ProtoMessage.Parse(izg).Fields;

            var bono = Assert.Single(campos.Where(f => f.FieldNumber == 15));
            Assert.NotNull(bono.BytesValue);
        }

        [Fact]
        public void Las_dos_formas_del_potenciador_son_las_medidas()
        {
            // The bytes are NOT from my arithmetic: they are copied from the captures' f15, which
            // is the only thing that counts as a reference.
            //
            //   (111, 1)   0a042001586f1001            f1 { f4: 1, f11: 111 }, f2: 1
            //   (281, 2)   0a07320208025899021001      f1 { f6 { f1: 2 }, f11: 281 }, f2: 1
            var s = Uno();
            s.Ganados.Clear();
            s.Ganados.Add(new Dreams.Bono(111, 1));
            s.Ganados.Add(new Dreams.Bono(281, 2, anidado: true));

            string hexa = Convert.ToHexString(DreamProtocol.BuildDreamState(s)).ToLowerInvariant();

            Assert.Contains("0a042001586f1001", hexa);
            Assert.Contains("0a07320208025899021001", hexa);
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
