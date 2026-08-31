using System;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    /// <summary>
    /// El cartel de partida encontrada, contra los bytes de las dos capturas.
    /// </summary>
    /// <remarks>
    /// Las dos son del servidor real y de modalidades distintas, que es lo que hace que se puedan
    /// contrastar: «koliseo completo con invitacion-koli 2vs2…» y «koliseo 3 vs 3 recibir mensaje
    /// aceptar koliseo-esperar timeout-comprobar sancion».
    /// </remarks>
    public class KoliseoOfferTests
    {
        [Fact]
        public void El_cartel_lleva_el_plazo_en_segundos()
        {
            // «103b» en las DOS capturas: f2 = 59. Que son segundos lo dice el reloj de la del
            // 3 contra 3, donde entre el cartel y el vencimiento pasan 60.014 ms.
            Assert.Equal("103b",
                Convert.ToHexString(KoliseoHandler.BuildOffer(KoliseoOffers.Segundos)).ToLowerInvariant());
            Assert.Equal(59, KoliseoOffers.Segundos);
        }

        [Fact]
        public void El_acuse_de_aceptar_es_un_booleano()
        {
            // «1001» de la captura del 2 contra 2. El campo 2 del lth es un bool, no un indice:
            // lo dice el esquema del propio cliente, lth { bool gdak = 1; bool gdal = 2; }.
            Assert.Equal("1001", Convert.ToHexString(KoliseoHandler.BuildAccepted(true)).ToLowerInvariant());

            // Y un falso de proto3 no viaja, asi que decir que no son cero bytes.
            Assert.Empty(KoliseoHandler.BuildAccepted(false));
        }

        [Fact]
        public void Salir_de_la_cola_lleva_la_modalidad()
        {
            // «18032001» en la captura del 2 contra 2 y «18032002» en la del 3 contra 3: mismo
            // mensaje, misma forma, y la modalidad en el f4.
            Assert.Equal("18032001", Convert.ToHexString(KoliseoHandler.BuildLeftQueue(1)).ToLowerInvariant());
            Assert.Equal("18032002", Convert.ToHexString(KoliseoHandler.BuildLeftQueue(2)).ToLowerInvariant());
        }

        [Fact]
        public void La_sancion_es_la_de_la_captura()
        {
            // «080110f703220a31373838323136393936»: f1 = 1, f2 = 503, y el f4 la marca de tiempo
            // en segundos COMO CADENA — 1788216996.
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

            // Y ya cerrada, un si que llegue tarde no la vuelve a arrancar.
            Assert.False(KoliseoOffers.Accept(oferta, 4));
        }

        [Fact]
        public void El_vencimiento_no_pisa_una_aceptacion_que_llego_por_los_pelos()
        {
            KoliseoOffers.ForgetEverything();
            var oferta = KoliseoOffers.Open(0, 1, new long[] { 7 }, new long[] { 8 });

            KoliseoOffers.Accept(oferta, 7);
            Assert.True(KoliseoOffers.Accept(oferta, 8));   // completa: queda cerrada

            // El reloj llega despues y tiene que encontrarsela cerrada, o montaria el combate y
            // lo desharia a la vez.
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

            // Cinco minutos justos: quedan cinco, no cuatro y pico.
            KoliseoOffers.Ban(11, DateTime.UtcNow.AddMinutes(5));
            Assert.Equal(5, KoliseoOffers.MinutesLeft(11));

            // Tres minutos y medio se leen como cuatro, que es lo que enseña la captura.
            KoliseoOffers.Ban(12, DateTime.UtcNow.AddSeconds(210));
            Assert.Equal(4, KoliseoOffers.MinutesLeft(12));

            // Y un castigo vencido no es castigo.
            KoliseoOffers.Ban(13, DateTime.UtcNow.AddSeconds(-1));
            Assert.Equal(0, KoliseoOffers.MinutesLeft(13));
            Assert.Null(KoliseoOffers.BannedUntil(13));
        }

        [Fact]
        public void Al_abrir_la_oferta_se_recuerda_la_modalidad()
        {
            // El lsx de volver del koliseo la necesita, y al volver ya no hay ni cola ni oferta.
            KoliseoOffers.ForgetEverything();
            KoliseoOffers.Open(2, 1, new long[] { 21 }, new long[] { 22 });

            Assert.Equal(2, KoliseoOffers.LastMode(21));
            Assert.Equal(2, KoliseoOffers.LastMode(22));
            Assert.Equal(0, KoliseoOffers.LastMode(999));
        }
    }
}
