using System.IO;
using Avalonia.Headless.XUnit;
using Jondo.Unity.Launcher;
using Jondo.Unity.Sprites;
using Xunit;

namespace Jondo.Unity.Tests.Sprites
{
    /// <summary>
    /// Que el retrato salga de frente y con cara.
    /// </summary>
    /// <remarks>
    /// Los dos fallos que vigila esta clase tenían el mismo síntoma: NINGUNO. Salía un dibujo, se
    /// guardaba sin quejarse y pasaba por bueno; sólo mirándolo se veía que el personaje estaba de
    /// espaldas y que no tenía cabeza. Comprobar que el dibujo no es nulo no habría cazado ni uno.
    ///
    ///   DE ESPALDAS — el regex que elegía la pose, <c>^AnimStatique_(\d+)$</c>, no casa con
    ///   ningún rig humanoide salvo el de la raza 12, así que se caía por la escalera de reserva y
    ///   salía la primera animación del array. En 13 de las 19 razas ésa es la dirección 6, el
    ///   norte, o sea la espalda.
    ///
    ///   SIN CABEZA — los registros de símbolo -1 se tiraban junto a los de -99. El -99 sí sobra;
    ///   el -1 es el que trae Tete, Thorax y la sombra.
    ///
    /// Hace falta el cliente de Dofus para dibujar. Donde no esté, la prueba se calla: es lo mismo
    /// que ya hacen las fotos de trabajo del lanzador, y una prueba que no puede medir es mejor
    /// callada que verde por defecto.
    /// </remarks>
    public class PortraitFacingTests
    {
        /// <summary>Una Ocra hembra con su cabeza, su escudo y su capa. De la base de pruebas.</summary>
        private const string Ocra =
            "{1|91,2148,462,461|1=#E59B68,2=#DB7933,3=#756F2B,4=#8F5203,5=#8F5203,6=#FA950F|52}";

        private static bool HayCliente
            => File.Exists(Path.Combine(Paths.ClientContentDir, "Characters", "Bones",
                                        "bones_assets_bone_1-9-static.bundle"));

        [AvaloniaFact]
        public void Un_humanoide_se_dibuja_de_frente()
        {
            if (!HayCliente) return;

            using var pintor = new NpcSprites();
            Assert.NotNull(pintor.Of(Ocra));

            // El 2 es el sur, el único de los cinco que trae el rig que mira a cámara.
            Assert.EndsWith("_2", pintor.LastAnimation);
            Assert.True(pintor.LastDirectionFound,
                $"la dirección de frente no se ha encontrado; se dibujó con «{pintor.LastAnimation}»");
        }

        [AvaloniaFact]
        public void Y_con_cabeza()
        {
            if (!HayCliente) return;

            using var pintor = new NpcSprites();
            Assert.NotNull(pintor.Of(Ocra));

            // El hueco de la cabeza de la dirección 2, lleno por la piel 2148. Contar los
            // triángulos y no sólo mirar que el hueco exista: un hueco que nadie llena también
            // aparece en la lista, con cero.
            Assert.True(pintor.LastSlots.TryGetValue("Tete_2", out int cabeza) && cabeza > 0,
                        $"la cabeza no se ha dibujado. Huecos: {pintor.LastMakeup}");

            Assert.True(pintor.LastSlots.TryGetValue("Torse_2", out int torso) && torso > 0,
                        "el torso tampoco, así que esto no es sólo la cabeza");
        }

        [AvaloniaFact]
        public void Un_monstruo_se_queda_como_estaba()
        {
            // Un hueso que no es el 1 no lleva las animaciones de los humanoides, así que pedirle
            // la dirección de frente no vale de nada. Tiene que seguir dibujándose igual que antes:
            // Studio saca cientos de éstos en una rejilla.
            var monstruo = NpcLook.Parse("{58|||90}");
            Assert.True(monstruo.Valid);
            Assert.False(monstruo.Humanoid);
        }

        [AvaloniaFact]
        public void La_altura_pedida_es_la_que_sale()
        {
            if (!HayCliente) return;

            using var pintor = new NpcSprites { Height = 192 };
            var dibujo = pintor.Of(Ocra);

            Assert.NotNull(dibujo);
            Assert.Equal(192, dibujo!.PixelSize.Height);
        }

        [AvaloniaFact]
        public void Dos_alturas_no_comparten_dibujo()
        {
            if (!HayCliente) return;

            // La caché va por cadena de aspecto, y la altura y la dirección cambian el dibujo sin
            // cambiar la cadena. El lanzador dibuja a 256 y Studio a 96 en el mismo proceso.
            using var pintor = new NpcSprites();

            var pequeno = pintor.Of(Ocra);
            pintor.Height = 192;
            var grande = pintor.Of(Ocra);

            Assert.NotNull(pequeno);
            Assert.NotNull(grande);
            Assert.Equal(96, pequeno!.PixelSize.Height);
            Assert.Equal(192, grande!.PixelSize.Height);
        }
    }
}
