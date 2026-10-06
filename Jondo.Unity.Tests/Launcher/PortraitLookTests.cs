using Jondo.Unity.Sprites;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// The look string the server sends the launcher for the team portrait.
    /// </summary>
    /// <remarks>
    /// They are TWO different forms and confusing them is what makes nothing get drawn:
    ///
    ///   - the base's <c>Look</c> column is the protobuf in hexadecimal, which is what travels to the
    ///     game client —«0801120CF5B7CB34…»—
    ///   - what the bone reader knows how to draw is the braces one,
    ///     <c>{bone|skins|colours|scale}</c>
    ///
    /// Sending the first believing it is the second does not fail: <c>NpcLook.Parse</c> takes it as
    /// invalid and the portrait comes out empty, without a single error anywhere.
    /// </remarks>
    public class PortraitLookTests
    {
        [Fact]
        public void El_hexadecimal_de_la_base_NO_se_puede_dibujar()
        {
            // Just as it is in a real character's Look column.
            const string deLaBase = "0801120CF5B7CB34888CA02892A6C82018032218A28B9B0FCBE5F615A4E1B919";

            Assert.False(NpcLook.Parse(deLaBase).Valid);
        }

        [Fact]
        public void La_de_llaves_si()
        {
            // The one the server composes: bone 1 —the humanoid rig, the playable ones'—, its
            // skins, the six colours and the scale.
            var look = NpcLook.Parse("{1|90,91|1=#FFFFFF,2=#62A1C9|53}");

            Assert.True(look.Valid);
            Assert.True(look.Humanoid);
            Assert.Equal(1, look.Bone);
            Assert.Equal(new[] { 90, 91 }, look.Skins);
            Assert.Equal(53, look.Scale);
        }

        [Fact]
        public void La_que_compone_el_servidor_lleva_el_cuerpo_delante_y_la_cabeza_detras()
        {
            // Female Cra with head 137, which is the one the test characters have. The id
            // at zero is "nobody": without a character in the base there is no equipment or cosmetics to add,
            // so what is left is exactly the body and the face.
            var quien = new Jondo.Unity.Server.DatabaseManager.DbCharacter
            {
                Id = 0, Name = "prueba", Breed = 9, Sex = 1, Level = 200, HeadId = 137,
            };

            var look = NpcLook.Parse(Jondo.Unity.Server.Managers.BreedLookTable.Drawable(quien));

            Assert.True(look.Valid);
            Assert.True(look.Humanoid);

            // THE FIRST SKIN IS THE BODY: it is where the breed for choosing the rig comes from. Putting
            // anything else in front -- the head, a hat -- the rig is not found and
            // nothing is drawn, without a single error.
            var suyo = Jondo.Unity.Server.Managers.BreedLookTable.Get(9, 1);
            Assert.NotNull(suyo);
            Assert.Equal((int)suyo!.Skins[0], look.Skins[0]);
            Assert.Equal(9, Breeds.Of(look.Skins[0]));

            // And the head goes after, added. Without it the character comes out without a face.
            int cabeza = Jondo.Unity.Server.Managers.HeadTable.SkinFor(137, 9, 1);
            Assert.True(cabeza > 0, "la cabeza 137 tiene que tener piel en heads.json");
            Assert.Contains(cabeza, look.Skins);
            Assert.NotEqual(cabeza, look.Skins[0]);
        }

        [Fact]
        public void Sin_raza_conocida_no_hay_cadena()
        {
            // A breed that does not exist cannot compose anything, and has to return empty instead of
            // a half string the reader would take as valid.
            var quien = new Jondo.Unity.Server.DatabaseManager.DbCharacter
            {
                Id = 0, Name = "prueba", Breed = 999, Sex = 0,
            };

            Assert.Equal("", Jondo.Unity.Server.Managers.BreedLookTable.Drawable(quien));
        }

        [Fact]
        public void Sin_aspecto_no_hay_retrato_y_no_pasa_nada()
        {
            // An account whose character the server cannot compose sends an empty string. It has to
            // be left without a portrait, not blow up: the card reads the same with the initial.
            Assert.False(NpcLook.Parse("").Valid);
            Assert.False(NpcLook.Parse(null).Valid);
        }
    }
}
