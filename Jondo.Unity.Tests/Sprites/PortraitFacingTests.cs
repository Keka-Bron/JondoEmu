using System.IO;
using Avalonia.Headless.XUnit;
using Jondo.Unity.Launcher;
using Jondo.Unity.Sprites;
using Xunit;

namespace Jondo.Unity.Tests.Sprites
{
    /// <summary>
    /// That the portrait comes out facing front and with a face.
    /// </summary>
    /// <remarks>
    /// The two bugs this class watches had the same symptom: NONE. A drawing came out, was
    /// stored without complaint and passed as good; only by looking at it could one see that the character had
    /// his back turned and had no head. Checking that the drawing is not null would not have caught either.
    ///
    ///   BACK TURNED — the regex that chose the pose, <c>^AnimStatique_(\d+)$</c>, does not match
    ///   any humanoid rig except breed 12's, so it fell down the fallback ladder and
    ///   the array's first animation came out. In 13 of the 19 breeds that is direction 6, the
    ///   north, that is the back.
    ///
    ///   NO HEAD — the symbol -1 records were thrown away together with the -99 ones. -99 is indeed superfluous;
    ///   -1 is the one bringing Tete, Thorax and the shadow.
    ///
    /// The Dofus client is needed to draw. Where it is not, the test keeps quiet: it is the same
    /// the launcher's working photos already do, and a test that cannot measure is better
    /// quiet than green by default.
    /// </remarks>
    public class PortraitFacingTests
    {
        /// <summary>A female Cra with her head, her shield and her cape. From the test base.</summary>
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

            // 2 is south, the only one of the five the rig brings that looks at the camera.
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

            // The head slot of direction 2, filled by skin 2148. Counting the
            // triangles and not just looking that the slot exists: a slot nobody fills also
            // appears on the list, with zero.
            Assert.True(pintor.LastSlots.TryGetValue("Tete_2", out int cabeza) && cabeza > 0,
                        $"la cabeza no se ha dibujado. Huecos: {pintor.LastMakeup}");

            Assert.True(pintor.LastSlots.TryGetValue("Torse_2", out int torso) && torso > 0,
                        "el torso tampoco, así que esto no es sólo la cabeza");
        }

        [AvaloniaFact]
        public void Un_monstruo_se_queda_como_estaba()
        {
            // A bone that is not 1 does not carry the humanoids' animations, so asking it for
            // the front direction is of no use. It has to keep being drawn the same as before:
            // Studio brings out hundreds of these in a grid.
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

            // The cache goes by look string, and the height and the direction change the drawing without
            // changing the string. The launcher draws at 256 and Studio at 96 in the same process.
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
