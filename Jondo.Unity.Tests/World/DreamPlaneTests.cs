using System.Linq;
using Jondo.Unity.Server.Managers;

using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Astral Plane: the clickable well and the four arches leading to Draconiros.
    /// </summary>
    /// <remarks>
    /// Everything checked here is measured in «entrar a sueños-hablar con draconiros…»,
    /// reading the f11 of the jss of map 238551040:
    ///
    ///   f11 { f1: 1, f4 { f1: 20744, f2: 360 }, f4 { f1: 20743, f2: 184 }, f5: 539616, f6: -1 }
    ///   f11 { f1: 1, f4 { f1: 20739, f2: 184 },                            f5: 539699, f6: -1 }
    ///   ... and the same for 539700, 539701 and 539702.
    ///
    /// f4.f1 is the instance uid —what the client returns in its iwo— and f4.f2 the
    /// skill. Announcing the uid in the skill's place is exactly what left the well
    /// as an ornament: the client knows no skill 20743, and an element whose skill does not
    /// exist cannot be clicked and does not give a single error.
    /// </remarks>
    [Collection("MapManager")]
    public class DreamPlaneTests
    {
        private const int DraconirosDelCrisol = 5130;

        [Fact]
        public void El_pozo_se_anuncia_con_una_habilidad_que_el_cliente_conoce()
        {
            // 184 is not a chosen number: it is the one with which a house is already entered and the
            // lottery used, so if this breaks something that works today breaks too.
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
            // Four measured goings, all four to the same map and the same cell.
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
            // The crucible's dragon brings down a single sentence and a single reply, and that reply
            // says nothing: it sends the player back. Without it the crucible was a dead end and
            // one had to go back by zaap.
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
            // The capture's iwn, field by field: «080110e0f72020b80128a28280c8e708».
            byte[] iwn = Jondo.Unity.Server.Network.ConnectionProtocol.BuildElementInUse(
                Dreams.ElementoDelPozo, Dreams.HabilidadDelPozo, 0x1c8e00280a2);

            string hexa = System.Convert.ToHexString(iwn).ToLowerInvariant();
            Assert.StartsWith("080110e0f72020b801", hexa);
        }

        [Fact]
        public void Las_puertas_del_sueno_usan_la_misma_habilidad_que_el_pozo()
        {
            // The iwn of door 539511 in the Dream III capture carries f4 = 184, the same as
            // the well's. A single skill for the whole Astral Plane.
            byte[] iwn = Jondo.Unity.Server.Network.ConnectionProtocol.BuildElementInUse(
                539511, Dreams.HabilidadDelPozo, 0x1c8e00280a2);

            Assert.StartsWith("080110f7f62020b801",
                              System.Convert.ToHexString(iwn).ToLowerInvariant());
        }

        [Fact]
        public void Las_puertas_de_las_salas_estan_declaradas()
        {
            // The same bug the well had, and in the same place: drawn but without an action, that
            // is the player enters the dream and has nowhere to go on.
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

            // The entrance's three are the capture's 539509, 539510 and 539511.
            var ids = entrada.Select(x => x.Element.Id).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 539509, 539510, 539511 }, ids);
        }

        [Fact]
        public void Y_de_la_sala_se_vuelve()
        {
            // Four measured returns, all four through the same element to cell 221.
            Interactives.Initialize();
            TeleportManager.Initialize();

            Assert.True(TeleportManager.TryGet(Dreams.MapaDeDraconiros, 539673, out var vuelta),
                        "la salida de la sala de Draconiros no está declarada");
            Assert.Equal(Dreams.MapaDelPozo, vuelta.DestinationMapId);
            Assert.Equal(221, vuelta.DestinationCellId);
        }
    }
}
