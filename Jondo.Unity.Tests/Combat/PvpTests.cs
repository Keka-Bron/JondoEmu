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
    /// Challenges between players, and the koliseo modes.
    /// </summary>
    /// <remarks>
    /// Measured in the five captures of the Combate folder: four of challenges, which between them
    /// cover accepting and declining from both sides, and one of a 2 versus 2 koliseo.
    ///
    /// What matters most to pin here is that accepting and declining are NOT two different opcodes: they are
    /// separated by a single field of the hpu, and confusing them would make declining set up the fight.
    /// </remarks>
    [Collection("koliseo")]
    public class PvpTests
    {
        public PvpTests()
        {
            Duels.ForgetEverything();
            KoliseoQueue.ForgetEverything();
        }

        // ------------------------------------------------------------------------ the challenges

        [Fact]
        public void El_desafio_ofrecido_lleva_a_los_dos_y_su_id()
        {
            // 08a28280c8e708 10a282f0a6c408 18ee03 from the capture, with that pair's ids.
            byte[] hqc = FightProtocol.BuildChallengeOffered(302677754146L, 293213045026L, 494);

            Assert.Equal("08a28280c8e70810a282f0a6c40818ee03",
                         Convert.ToHexString(hqc).ToLowerInvariant());
        }

        [Fact]
        public void Aceptar_y_rechazar_solo_se_diferencian_en_un_campo()
        {
            // The capture's two hpv, byte for byte. f3 is in the accepted one and not in the other,
            // and the challenged goes in FOUR: in the declined one the 20 goes stuck to the id.
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
            // The hpu arrives repeated in two of the captures. Taking it off the list on answering is what
            // keeps two answers from setting up two fights.
            var desafio = Duels.Open(1, 2, 100);

            Assert.NotNull(Duels.Take(desafio.Id));
            Assert.Null(Duels.Take(desafio.Id));
        }

        [Fact]
        public void Nadie_anda_en_dos_a_la_vez()
        {
            // Without this the same one can be challenged a hundred times and his screen filled with windows.
            Duels.Open(1, 2, 100);

            Assert.True(Duels.Busy(1));
            Assert.True(Duels.Busy(2));
            Assert.False(Duels.Busy(3));
        }

        [Fact]
        public void Al_desconectarse_se_le_cierran_los_suyos()
        {
            // A challenge whose challenger is no longer there is a window that cannot be answered.
            Duels.Open(1, 2, 100);
            Duels.Open(3, 4, 100);

            Assert.Equal(1, Duels.ForgetThoseOf(2));
            Assert.False(Duels.Busy(1));
            Assert.True(Duels.Busy(3));
        }

        // --------------------------------------------------------------------------- the koliseo

        [Fact]
        public void Las_tres_modalidades_estan_abiertas()
        {
            // What was asked for: 1 versus 1, 2 versus 2 and 3 versus 3; and the fourth card, 1 versus 1
            // against a JondoBot.
            Assert.Equal(4, KoliseoHandler.CountOpen());

            foreach (int equipos in new[] { 1, 2, 3 })
            {
                Assert.Contains(KoliseoHandler.Modes,
                                modo => modo.Open && modo.TeamSize == equipos);
            }
        }

        [Fact]
        public void La_tabla_es_la_de_la_captura()
        {
            // Byte for byte the ltd of «koliseo completo con invitacion-koli 2vs2» in the first
            // three. The fourth arrives closed, a 3 versus 3; here it is the JondoBots' card,
            // open and 1 versus 1 (see KoliseoBotTests).
            byte[] ltd = KoliseoHandler.BuildModes(KoliseoHandler.Modes);

            Assert.StartsWith("0a0812040801200118010a0a080112040801200218010a0a0802120408012003" +
                              "18010a",
                              Convert.ToHexString(ltd).ToLowerInvariant());
        }

        // ------------------------------------------------------ each client's preparation

        [Fact]
        public void La_preparacion_se_recuerda_por_combatiente_y_no_por_combate()
        {
            // This was a single boolean of the fight —HasLoadedMap— and that is why in a challenge the
            // second client to load the map was left without fighters and without a ready button:
            // the first to send its kmv set the flag and the other was answered that there was no
            // preparation pending any more. It is a race, so it was not always the same one that failed.
            var combate = new FightInstance(1, 100, 200);

            Assert.True(combate.MarkPrepared(10));
            Assert.False(combate.MarkPrepared(10));   // the same one, again: it is already served

            // And the other has his, which is exactly what was missing.
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
            // They arrive through two different connections and are handled on two threads. Without a lock, two
            // simultaneous MarkPrepared can lose one of the two and leave someone without
            // preparation —or send it to him twice—.
            var combate = new FightInstance(1, 100, 200);
            var concedidos = new System.Collections.Concurrent.ConcurrentBag<bool>();

            System.Threading.Tasks.Parallel.For(0, 64, i =>
                concedidos.Add(combate.MarkPrepared(i % 2 == 0 ? 10 : 20)));

            // Sixty-four attempts over two fighters: exactly two get it.
            Assert.Equal(2, concedidos.Count(c => c));
        }

        [Fact]
        public void El_turno_se_abre_una_sola_vez_aunque_contesten_los_dos()
        {
            // The «confirm to me» goes to both clients and both answer. What hangs from that
            // answer —undoing expired summons, sweeping buffs, giving back points— has to
            // happen once: with two, the points were given back twice.
            var combate = new FightInstance(1, 100, 200);

            Assert.True(combate.AtenderElTurnoUnaVez(1, 0));
            Assert.False(combate.AtenderElTurnoUnaVez(1, 0));

            // And the next turn opens again, otherwise the fight stops at the first.
            Assert.True(combate.AtenderElTurnoUnaVez(1, 1));
            Assert.True(combate.AtenderElTurnoUnaVez(2, 0));
        }

        /// <summary>
        /// The same index of the same round is somebody else's turn once the order is rebuilt: the
        /// Ocra at index 2, a fighter before him gone, his Arakna summoned at index 2. Her turn
        /// opens; taken for his, it never did.
        /// </summary>
        [Fact]
        public void A_turn_at_an_index_already_confirmed_opens_when_it_is_somebody_elses()
        {
            var combate = new FightInstance(1, 100, 200);
            Assert.True(combate.AtenderElTurnoUnaVez(3, 2, 13825558));
            Assert.False(combate.AtenderElTurnoUnaVez(3, 2, 13825558));
            Assert.True(combate.AtenderElTurnoUnaVez(3, 2, -3));
        }

        // -------------------------------------------------------- signing up and matchmaking

        [Fact]
        public void El_estado_de_la_cola_es_el_lsx_de_la_captura()
        {
            // «08012001», the lsx the real server pushes 27 seconds after entering without
            // the client asking for anything: f1 true, f4 one. The client's schema says
            // lsx { bool gcyt = 1; ... lsg gcyw = 4; }, that is «searching» and «in which», and lsg is
            // the enum of the four modes. 1 is the two versus two.
            Assert.Equal("08012001",
                         Convert.ToHexString(KoliseoHandler.BuildQueueState(1, true)).ToLowerInvariant());
        }

        [Fact]
        public void La_modalidad_cero_no_viaja_en_el_lsx()
        {
            // The one versus one is the enum's zero value, and protobuf does not send zeros.
            // Searching in the one versus one is two bytes: only the «yes».
            Assert.Equal("0801",
                         Convert.ToHexString(KoliseoHandler.BuildQueueState(0, true)).ToLowerInvariant());
        }

        [Fact]
        public void Dejar_de_buscar_no_lleva_el_si()
        {
            // False is a bool's default value and does not travel either.
            Assert.Empty(KoliseoHandler.BuildQueueState(0, false));
        }

        [Fact]
        public void El_lsx_de_la_vuelta_es_el_de_la_captura()
        {
            // «18032001», the one that answers the lte after 80 ms.
            // The mode goes in f4, the same as in the lsx of being searching: «18032001» in the
            // 2 versus 2 capture and «18032002» in the 3 versus 3 one.
            Assert.Equal("18032001", Convert.ToHexString(KoliseoHandler.BuildLeftQueue(1)).ToLowerInvariant());
            Assert.Equal("18032002", Convert.ToHexString(KoliseoHandler.BuildLeftQueue(2)).ToLowerInvariant());
        }

        [Fact]
        public void Nadie_se_apunta_dos_veces()
        {
            Assert.True(KoliseoQueue.Enrol(1, 1));
            Assert.False(KoliseoQueue.Enrol(1, 1));

            // Not even by changing mode: if it were allowed, a single one would fill the three queues.
            Assert.False(KoliseoQueue.Enrol(1, 2));
            Assert.Equal(1, KoliseoQueue.Count);
        }

        [Fact]
        public void Cada_modalidad_tiene_su_cola()
        {
            KoliseoQueue.Enrol(1, 0);
            KoliseoQueue.Enrol(2, 1);

            // Whoever waits for a 3 versus 3 is no use to fill a 1 versus 1.
            Assert.Equal(1, KoliseoQueue.CountIn(0));
            Assert.Equal(1, KoliseoQueue.CountIn(1));
            Assert.Equal(0, KoliseoQueue.CountIn(2));
        }

        [Fact]
        public void No_hay_partida_hasta_que_estan_los_dos_equipos()
        {
            for (int i = 1; i <= 3; i++) KoliseoQueue.Enrol(i, 1);

            // Three for a two versus two are three, not a match and a half.
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

            // And they leave the queue: the fifth stays waiting for the next.
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

            // And whoever was not there leaves none.
            Assert.Equal(-1, KoliseoQueue.Leave(7));
        }

        [Fact]
        public void El_koliseo_se_anuncia_como_tipo_siete_y_con_reloj()
        {
            // «1801200128d0043007» from the capture: f3=1 f4=1 f5=592 f6=7. The challenge brings neither
            // f5 nor f6, and that is exactly the difference between the two.
            byte[] kaa = FightProtocol.BuildFightSummary(
                FightProtocol.Koliseo, FightProtocol.KoliseoPlacementDeciseconds);

            Assert.Equal("1801200128d0043007", Convert.ToHexString(kaa).ToLowerInvariant());
            Assert.Equal(7, FightProtocol.Koliseo);
        }

    }
}
