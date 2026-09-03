using System.Linq;
using Jondo.Unity.Server.Managers;

using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// El Plano Astral: el pozo pulsable y las cuatro arcadas que llevan a Draconiros.
    /// </summary>
    /// <remarks>
    /// Todo lo que se comprueba aquí está medido en «entrar a sueños-hablar con draconiros…»,
    /// leyendo el f11 del jss del mapa 238551040:
    ///
    ///   f11 { f1: 1, f4 { f1: 20744, f2: 360 }, f4 { f1: 20743, f2: 184 }, f5: 539616, f6: -1 }
    ///   f11 { f1: 1, f4 { f1: 20739, f2: 184 },                            f5: 539699, f6: -1 }
    ///   ... y lo mismo para 539700, 539701 y 539702.
    ///
    /// El f4.f1 es el uid de instancia —lo que el cliente devuelve en su iwo— y el f4.f2 la
    /// habilidad. Anunciar el uid en el sitio de la habilidad es exactamente lo que dejó el pozo
    /// de adorno: el cliente no conoce ninguna habilidad 20743, y un elemento cuya habilidad no
    /// existe no se puede pulsar y no da un solo error.
    /// </remarks>
    [Collection("MapManager")]
    public class DreamPlaneTests
    {
        private const int DraconirosDelCrisol = 5130;

        [Fact]
        public void El_pozo_se_anuncia_con_una_habilidad_que_el_cliente_conoce()
        {
            // La 184 no es un número elegido: es con la que ya se entra en una casa y se usa la
            // lotería, así que si esto se rompe se rompe también algo que hoy funciona.
            Assert.Equal(184, Dreams.HabilidadDelPozo);
            Assert.Equal(Houses.ExitSkill, Dreams.HabilidadDelPozo);
            Assert.NotEqual(20743, Dreams.HabilidadDelPozo);
        }

        [Fact]
        public void El_pozo_esta_registrado_como_pulsable()
        {
            Interactives.Initialize();
            TeleportManager.Initialize();
            InteractiveRegistry.Initialize();

            RegisteredInteractive? pozo = null;
            foreach (var declarado in InteractiveRegistry.OnMap(Dreams.MapaDelPozo))
            {
                if (declarado.Element.Id == Dreams.ElementoDelPozo) pozo = declarado;
            }

            Assert.NotNull(pozo);
            Assert.Equal(Dreams.TipoDelPozo, pozo!.Type);

            Assert.Contains(pozo.Actions, a => a.Kind == InteractiveActionKind.Dream
                                            && a.SkillId == Dreams.HabilidadDelPozo);
        }

        [Theory]
        [InlineData(539699)]
        [InlineData(539700)]
        [InlineData(539701)]
        [InlineData(539702)]
        public void Cada_arcada_lleva_a_la_sala_de_Draconiros(int elemento)
        {
            // Cuatro idas medidas, las cuatro al mismo mapa y a la misma casilla.
            Interactives.Initialize();
            TeleportManager.Initialize();

            Assert.True(TeleportManager.TryGet(Dreams.MapaDelPozo, elemento, out var ruta),
                        $"la arcada {elemento} no está declarada como pasaje");
            Assert.Equal(Dreams.MapaDeDraconiros, ruta.DestinationMapId);
            Assert.Equal(381, ruta.DestinationCellId);
            Assert.Equal(184, ruta.SkillId);
        }

        [Fact]
        public void Del_crisol_se_sale_por_donde_se_entro()
        {
            // El dragón del crisol baja una sola frase y una sola respuesta, y esa respuesta no
            // dice nada: devuelve al jugador. Sin ella el crisol era un callejón sin salida y
            // había que volver a zaaps.
            Npcs.Initialize();

            var charla = NpcDialogues.For(DraconirosDelCrisol, 0);
            Assert.NotNull(charla);
            Assert.Equal(38840, charla!.Opening);

            var frase = charla.Line(38840);
            Assert.NotNull(frase);
            var salir = Assert.Single(frase!.Choices);
            Assert.Equal(50989, salir.Reply);
            Assert.True(salir.ReturnsHome);
            Assert.Equal(0, salir.TeleportsTo);
        }

        [Fact]
        public void El_pozo_se_suelta_antes_de_ensenar_la_ventana()
        {
            // El iwn de la captura, campo a campo: «080110e0f72020b80128a28280c8e708».
            byte[] iwn = Jondo.Unity.Server.Network.ConnectionProtocol.BuildElementInUse(
                Dreams.ElementoDelPozo, Dreams.HabilidadDelPozo, 0x1c8e00280a2);

            string hexa = System.Convert.ToHexString(iwn).ToLowerInvariant();
            Assert.StartsWith("080110e0f72020b801", hexa);
        }

        [Fact]
        public void Las_puertas_del_sueno_usan_la_misma_habilidad_que_el_pozo()
        {
            // El iwn de la puerta 539511 en la captura de Sueño III lleva f4 = 184, el mismo que
            // el del pozo. Una sola habilidad para todo el Plano Astral.
            byte[] iwn = Jondo.Unity.Server.Network.ConnectionProtocol.BuildElementInUse(
                539511, Dreams.HabilidadDelPozo, 0x1c8e00280a2);

            Assert.StartsWith("080110f7f62020b801",
                              System.Convert.ToHexString(iwn).ToLowerInvariant());
        }

        [Fact]
        public void Las_puertas_de_las_salas_estan_declaradas()
        {
            // El mismo fallo que tuvo el pozo, y en el mismo sitio: dibujadas pero sin acción, o
            // sea que el jugador entra en el sueño y no tiene por dónde seguir.
            Interactives.Initialize();
            TeleportManager.Initialize();
            InteractiveRegistry.Initialize();

            var entrada = InteractiveRegistry.OnMap(Dreams.MapaDeEntrada);
            Assert.Equal(3, entrada.Count);

            foreach (var puerta in entrada)
            {
                Assert.Equal(Dreams.TipoDelPozo, puerta.Type);
                Assert.Contains(puerta.Actions, a => a.Kind == InteractiveActionKind.DreamDoor
                                                  && a.SkillId == 184);
            }

            // Las tres de la entrada son las 539509, 539510 y 539511 de la captura.
            var ids = entrada.Select(x => x.Element.Id).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 539509, 539510, 539511 }, ids);
        }

        [Fact]
        public void Y_de_la_sala_se_vuelve()
        {
            // Cuatro vueltas medidas, las cuatro por el mismo elemento a la casilla 221.
            Interactives.Initialize();
            TeleportManager.Initialize();

            Assert.True(TeleportManager.TryGet(Dreams.MapaDeDraconiros, 539673, out var vuelta),
                        "la salida de la sala de Draconiros no está declarada");
            Assert.Equal(Dreams.MapaDelPozo, vuelta.DestinationMapId);
            Assert.Equal(221, vuelta.DestinationCellId);
        }
    }
}
