using System;
using System.Linq;
using Jondo.Unity.World.Fights;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Los desafíos entre jugadores, y las modalidades del koliseo.
    /// </summary>
    /// <remarks>
    /// Medido en las cinco capturas de la carpeta Combate: cuatro de desafío, que entre ellas
    /// cubren aceptar y rechazar desde los dos lados, y una de koliseo 2 contra 2.
    ///
    /// Lo que más importa fijar aquí es que aceptar y rechazar NO son dos opcodes distintos: los
    /// separa un solo campo del hpu, y confundirlos haría que rechazar montase el combate.
    /// </remarks>
    public class PvpTests
    {
        public PvpTests()
        {
            Duels.ForgetEverything();
            KoliseoQueue.ForgetEverything();
        }

        // ------------------------------------------------------------------------ los desafíos

        [Fact]
        public void El_desafio_ofrecido_lleva_a_los_dos_y_su_id()
        {
            // 08a28280c8e708 10a282f0a6c408 18ee03 de la captura, con los ids de aquel par.
            byte[] hqc = FightProtocol.BuildChallengeOffered(302677754146L, 293213045026L, 494);

            Assert.Equal("08a28280c8e70810a282f0a6c40818ee03",
                         Convert.ToHexString(hqc).ToLowerInvariant());
        }

        [Fact]
        public void Aceptar_y_rechazar_solo_se_diferencian_en_un_campo()
        {
            // Los dos hpv de la captura, byte por byte. El f3 está en el aceptado y no en el otro,
            // y el retado va en el CUATRO: en el rechazado el 20 va pegado al id.
            byte[] aceptado = FightProtocol.BuildChallengeAnswered(302677754146L, 494, true, 293213045026L);
            byte[] rechazado = FightProtocol.BuildChallengeAnswered(302677754146L, 489, false, 293213045026L);

            Assert.Equal("08a28280c8e70810ee03180120a282f0a6c408",
                         Convert.ToHexString(aceptado).ToLowerInvariant());
            Assert.Equal("08a28280c8e70810e90320a282f0a6c408",
                         Convert.ToHexString(rechazado).ToLowerInvariant());
        }

        [Fact]
        public void Un_desafio_se_contesta_una_sola_vez()
        {
            // El hpu llega repetido en dos de las capturas. Sacarlo de la lista al contestar es lo
            // que impide que dos respuestas monten dos combates.
            var desafio = Duels.Open(1, 2, 100);

            Assert.NotNull(Duels.Take(desafio.Id));
            Assert.Null(Duels.Take(desafio.Id));
        }

        [Fact]
        public void Nadie_anda_en_dos_a_la_vez()
        {
            // Sin esto se puede retar cien veces al mismo y llenarle la pantalla de ventanas.
            Duels.Open(1, 2, 100);

            Assert.True(Duels.Busy(1));
            Assert.True(Duels.Busy(2));
            Assert.False(Duels.Busy(3));
        }

        [Fact]
        public void Al_desconectarse_se_le_cierran_los_suyos()
        {
            // Un desafío cuyo retador ya no está es una ventana que no se puede contestar.
            Duels.Open(1, 2, 100);
            Duels.Open(3, 4, 100);

            Assert.Equal(1, Duels.ForgetThoseOf(2));
            Assert.False(Duels.Busy(1));
            Assert.True(Duels.Busy(3));
        }

        // --------------------------------------------------------------------------- el koliseo

        [Fact]
        public void Las_tres_modalidades_estan_abiertas()
        {
            // Lo que se pedía: 1 contra 1, 2 contra 2 y 3 contra 3.
            Assert.Equal(3, KoliseoHandler.CountOpen());

            foreach (int equipos in new[] { 1, 2, 3 })
            {
                Assert.Contains(KoliseoHandler.Modes,
                                modo => modo.Open && modo.TeamSize == equipos);
            }
        }

        [Fact]
        public void La_tabla_es_la_de_la_captura()
        {
            // Byte por byte el ltd de «koliseo completo con invitacion-koli 2vs2». Las tres
            // primeras abiertas y la cuarta cerrada, que es como llega: encenderla sería inventar
            // una modalidad que nadie ha visto funcionar.
            byte[] ltd = KoliseoHandler.BuildModes(KoliseoHandler.Modes);

            Assert.Equal("0a0812040801200118010a0a080112040801200218010a0a0802120408012003" +
                         "18010a06080312022003",
                         Convert.ToHexString(ltd).ToLowerInvariant());
        }

        // ------------------------------------------------------ la preparación de cada cliente

        [Fact]
        public void La_preparacion_se_recuerda_por_combatiente_y_no_por_combate()
        {
            // Esto era un solo booleano del combate —HasLoadedMap— y por eso en un desafío el
            // segundo cliente en cargar el mapa se quedaba sin combatientes y sin botón de listo:
            // el primero en mandar su kmv ponía la bandera y al otro se le contestaba que ya no
            // había preparación pendiente. Es una carrera, así que no fallaba siempre el mismo.
            var combate = new FightInstance(1, 100, 200);

            Assert.True(combate.MarkPrepared(10));
            Assert.False(combate.MarkPrepared(10));   // el mismo, otra vez: ya está servido

            // Y el otro tiene la suya, que es justo lo que faltaba.
            Assert.True(combate.MarkPrepared(20));

            Assert.True(combate.HasPrepared(10));
            Assert.True(combate.HasPrepared(20));
            Assert.False(combate.HasPrepared(30));
        }

        [Fact]
        public void Olvidar_la_preparacion_de_uno_no_toca_la_del_otro()
        {
            var combate = new FightInstance(1, 100, 200);
            combate.MarkPrepared(10);
            combate.MarkPrepared(20);

            combate.ForgetPreparation(10);

            Assert.False(combate.HasPrepared(10));
            Assert.True(combate.HasPrepared(20));
        }

        [Fact]
        public void Los_dos_clientes_pueden_prepararse_a_la_vez()
        {
            // Llegan por dos conexiones distintas y se atienden en dos hilos. Sin candado, dos
            // MarkPrepared simultáneos pueden perder uno de los dos y dejar a alguien sin
            // preparación —o mandársela dos veces—.
            var combate = new FightInstance(1, 100, 200);
            var concedidos = new System.Collections.Concurrent.ConcurrentBag<bool>();

            System.Threading.Tasks.Parallel.For(0, 64, i =>
                concedidos.Add(combate.MarkPrepared(i % 2 == 0 ? 10 : 20)));

            // Sesenta y cuatro intentos sobre dos combatientes: exactamente dos se lo llevan.
            Assert.Equal(2, concedidos.Count(c => c));
        }

        [Fact]
        public void El_turno_se_abre_una_sola_vez_aunque_contesten_los_dos()
        {
            // El «confírmame» va a los dos clientes y contestan los dos. Lo que cuelga de esa
            // respuesta —deshacer invocados vencidos, barrer embrujos, devolver puntos— tiene que
            // pasar una vez: con dos, los puntos se devolvían dos veces.
            var combate = new FightInstance(1, 100, 200);

            Assert.True(combate.AtenderElTurnoUnaVez(1, 0));
            Assert.False(combate.AtenderElTurnoUnaVez(1, 0));

            // Y el turno siguiente vuelve a abrirse, que si no el combate se para en el primero.
            Assert.True(combate.AtenderElTurnoUnaVez(1, 1));
            Assert.True(combate.AtenderElTurnoUnaVez(2, 0));
        }

        // -------------------------------------------------------- apuntarse y el emparejamiento

        [Fact]
        public void El_estado_de_la_cola_es_el_lsx_de_la_captura()
        {
            // «08012001», el lsx que el servidor real empuja a los 27 segundos de entrar sin que
            // el cliente pida nada: f1 cierto, f4 uno. El esquema del cliente dice
            // lsx { bool gcyt = 1; ... lsg gcyw = 4; }, o sea «buscando» y «en cual», y el lsg es
            // el enumerado de las cuatro modalidades. El 1 es el dos contra dos.
            Assert.Equal("08012001",
                         Convert.ToHexString(KoliseoHandler.BuildQueueState(1, true)).ToLowerInvariant());
        }

        [Fact]
        public void La_modalidad_cero_no_viaja_en_el_lsx()
        {
            // El uno contra uno es el valor cero del enumerado, y protobuf no manda los ceros.
            // Buscando en el uno contra uno son dos bytes: solo el «si».
            Assert.Equal("0801",
                         Convert.ToHexString(KoliseoHandler.BuildQueueState(0, true)).ToLowerInvariant());
        }

        [Fact]
        public void Dejar_de_buscar_no_lleva_el_si()
        {
            // Falso es el valor por omision de un bool y tampoco viaja.
            Assert.Empty(KoliseoHandler.BuildQueueState(0, false));
        }

        [Fact]
        public void El_lsx_de_la_vuelta_es_el_de_la_captura()
        {
            // «18032001», el que contesta al lte a los 80 ms.
            // La modalidad va en el f4, igual que en el lsx de estar buscando: «18032001» en la
            // captura del 2 contra 2 y «18032002» en la del 3 contra 3.
            Assert.Equal("18032001", Convert.ToHexString(KoliseoHandler.BuildLeftQueue(1)).ToLowerInvariant());
            Assert.Equal("18032002", Convert.ToHexString(KoliseoHandler.BuildLeftQueue(2)).ToLowerInvariant());
        }

        [Fact]
        public void Nadie_se_apunta_dos_veces()
        {
            Assert.True(KoliseoQueue.Enrol(1, 1));
            Assert.False(KoliseoQueue.Enrol(1, 1));

            // Ni cambiando de modalidad: si valiera, uno solo llenaría las tres colas.
            Assert.False(KoliseoQueue.Enrol(1, 2));
            Assert.Equal(1, KoliseoQueue.Count);
        }

        [Fact]
        public void Cada_modalidad_tiene_su_cola()
        {
            KoliseoQueue.Enrol(1, 0);
            KoliseoQueue.Enrol(2, 1);

            // Quien espera un 3 contra 3 no sirve para llenar un 1 contra 1.
            Assert.Equal(1, KoliseoQueue.CountIn(0));
            Assert.Equal(1, KoliseoQueue.CountIn(1));
            Assert.Equal(0, KoliseoQueue.CountIn(2));
        }

        [Fact]
        public void No_hay_partida_hasta_que_estan_los_dos_equipos()
        {
            for (int i = 1; i <= 3; i++) KoliseoQueue.Enrol(i, 1);

            // Tres para un dos contra dos son tres, no una partida y medio.
            Assert.Null(KoliseoQueue.TryMatch(1, 2));
            Assert.Equal(3, KoliseoQueue.CountIn(1));
        }

        [Fact]
        public void La_partida_reparte_por_orden_de_llegada()
        {
            for (int i = 1; i <= 5; i++) KoliseoQueue.Enrol(i, 1);

            var partida = KoliseoQueue.TryMatch(1, 2);
            Assert.NotNull(partida);
            Assert.Equal(new long[] { 1, 2 }, partida!.Value.Blue);
            Assert.Equal(new long[] { 3, 4 }, partida.Value.Red);

            // Y salen de la cola: el quinto se queda esperando al siguiente.
            Assert.Equal(1, KoliseoQueue.CountIn(1));
            Assert.False(KoliseoQueue.Waits(1));
            Assert.True(KoliseoQueue.Waits(5));
        }

        [Fact]
        public void Salirse_lo_saca_de_la_cola_que_sea()
        {
            KoliseoQueue.Enrol(7, 2);

            Assert.Equal(2, KoliseoQueue.Leave(7));
            Assert.Equal(0, KoliseoQueue.Count);

            // Y el que no estaba no sale de ninguna.
            Assert.Equal(-1, KoliseoQueue.Leave(7));
        }

        [Fact]
        public void El_koliseo_se_anuncia_como_tipo_siete_y_con_reloj()
        {
            // «1801200128d0043007» de la captura: f3=1 f4=1 f5=592 f6=7. El desafío no trae ni el
            // f5 ni el f6, y ésa es justo la diferencia entre los dos.
            byte[] kaa = FightProtocol.BuildFightSummary(
                FightProtocol.Koliseo, FightProtocol.KoliseoPlacementDeciseconds);

            Assert.Equal("1801200128d0043007", Convert.ToHexString(kaa).ToLowerInvariant());
            Assert.Equal(7, FightProtocol.Koliseo);
        }

    }
}
