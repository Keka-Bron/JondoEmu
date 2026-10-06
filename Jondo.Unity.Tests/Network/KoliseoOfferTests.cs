using System;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    /// <summary>
    /// The match-found pop-up, against the bytes of the two captures.
    /// </summary>
    /// <remarks>
    /// Both are from the real server and of different modes, which is what allows comparing
    /// them: «koliseo completo con invitacion-koli 2vs2…» and «koliseo 3 vs 3 recibir mensaje
    /// aceptar koliseo-esperar timeout-comprobar sancion».
    /// </remarks>
    public class KoliseoOfferTests
    {
        [Fact]
        public void El_cartel_lleva_el_plazo_en_segundos()
        {
            // «103b» in BOTH captures: f2 = 59. That they are seconds is said by the clock of the
            // 3 versus 3 one, where 60,014 ms pass between the pop-up and the expiry.
            Assert.Equal("103b",
                Convert.ToHexString(KoliseoHandler.BuildOffer(KoliseoOffers.Segundos)).ToLowerInvariant());
            Assert.Equal(59, KoliseoOffers.Segundos);
        }

        [Fact]
        public void El_acuse_de_aceptar_es_un_booleano()
        {
            // «1001» from the 2 versus 2 capture. The lth's field 2 is a bool, not an index:
            // the client's own schema says so, lth { bool gdak = 1; bool gdal = 2; }.
            Assert.Equal("1001", Convert.ToHexString(KoliseoHandler.BuildAccepted(true)).ToLowerInvariant());

            // And a proto3 false does not travel, so saying no is zero bytes.
            Assert.Empty(KoliseoHandler.BuildAccepted(false));
        }

        [Fact]
        public void Salir_de_la_cola_lleva_la_modalidad()
        {
            // «18032001» in the 2 versus 2 capture and «18032002» in the 3 versus 3 one: same
            // message, same shape, and the mode in f4.
            Assert.Equal("18032001", Convert.ToHexString(KoliseoHandler.BuildLeftQueue(1)).ToLowerInvariant());
            Assert.Equal("18032002", Convert.ToHexString(KoliseoHandler.BuildLeftQueue(2)).ToLowerInvariant());
        }

        [Fact]
        public void La_sancion_es_la_de_la_captura()
        {
            // «080110f703220a31373838323136393936»: f1 = 1, f2 = 503, and f4 the timestamp
            // in seconds AS A STRING — 1788216996.
            Assert.Equal("080110f703220a31373838323136393936",
                Convert.ToHexString(KoliseoHandler.BuildSanction(1788216996L)).ToLowerInvariant());
        }

        [Fact]
        public void Reintentar_castigado_dice_los_minutos()
        {
            // «0801108205220134»: f1 = 1, f2 = 642, f4 = «4».
            Assert.Equal("0801108205220134",
                Convert.ToHexString(KoliseoHandler.BuildStillBanned(4)).ToLowerInvariant());
        }

        [Fact]
        public void Una_oferta_solo_arranca_cuando_han_dicho_que_si_todos()
        {
            KoliseoOffers.ForgetEverything();
            var oferta = KoliseoOffers.Open(1, 2, new long[] { 1, 2 }, new long[] { 3, 4 });

            Assert.False(KoliseoOffers.Accept(oferta, 1));
            Assert.False(KoliseoOffers.Accept(oferta, 2));
            Assert.False(KoliseoOffers.Accept(oferta, 3));
            Assert.True(KoliseoOffers.Accept(oferta, 4));

            // And once closed, a yes arriving late does not start it again.
            Assert.False(KoliseoOffers.Accept(oferta, 4));
        }

        [Fact]
        public void El_vencimiento_no_pisa_una_aceptacion_que_llego_por_los_pelos()
        {
            KoliseoOffers.ForgetEverything();
            var oferta = KoliseoOffers.Open(0, 1, new long[] { 7 }, new long[] { 8 });

            KoliseoOffers.Accept(oferta, 7);
            Assert.True(KoliseoOffers.Accept(oferta, 8));   // completa: queda cerrada

            // The clock arrives afterwards and has to find it closed, or it would set up the fight and
            // undo it at the same time.
            Assert.False(KoliseoOffers.Close(oferta));
        }

        [Fact]
        public void Quien_no_contesta_es_el_que_se_lleva_el_castigo()
        {
            KoliseoOffers.ForgetEverything();
            var oferta = KoliseoOffers.Open(2, 3, new long[] { 1, 2, 3 }, new long[] { 4, 5, 6 });

            KoliseoOffers.Accept(oferta, 1);
            KoliseoOffers.Accept(oferta, 4);

            Assert.Equal(new long[] { 2, 3, 5, 6 }, KoliseoOffers.WhoDidNotAnswer(oferta));
        }

        [Fact]
        public void Los_minutos_que_faltan_se_redondean_hacia_arriba()
        {
            KoliseoOffers.ForgetEverything();

            // Exactly five minutes: five are left, not four and a bit.
            KoliseoOffers.Ban(11, DateTime.UtcNow.AddMinutes(5));
            Assert.Equal(5, KoliseoOffers.MinutesLeft(11));

            // Three and a half minutes read as four, which is what the capture shows.
            KoliseoOffers.Ban(12, DateTime.UtcNow.AddSeconds(210));
            Assert.Equal(4, KoliseoOffers.MinutesLeft(12));

            // And an expired penalty is not a penalty.
            KoliseoOffers.Ban(13, DateTime.UtcNow.AddSeconds(-1));
            Assert.Equal(0, KoliseoOffers.MinutesLeft(13));
            Assert.Null(KoliseoOffers.BannedUntil(13));
        }

        [Fact]
        public void Al_abrir_la_oferta_se_recuerda_la_modalidad()
        {
            // The lsx of coming back from the koliseo needs it, and on coming back there is neither queue nor offer.
            KoliseoOffers.ForgetEverything();
            KoliseoOffers.Open(2, 1, new long[] { 21 }, new long[] { 22 });

            Assert.Equal(2, KoliseoOffers.LastMode(21));
            Assert.Equal(2, KoliseoOffers.LastMode(22));
            Assert.Equal(0, KoliseoOffers.LastMode(999));
        }
    }
}
