using Jondo.Unity.Sprites;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// La cadena de aspecto que el servidor manda al lanzador para el retrato del equipo.
    /// </summary>
    /// <remarks>
    /// Son DOS formas distintas y confundirlas es lo que hace que no se dibuje nada:
    ///
    ///   - la columna <c>Look</c> de la base es el protobuf en hexadecimal, que es lo que viaja al
    ///     cliente de juego —«0801120CF5B7CB34…»—
    ///   - lo que sabe dibujar el lector de huesos es la de las llaves,
    ///     <c>{hueso|pieles|colores|escala}</c>
    ///
    /// Mandar la primera creyendo que es la segunda no falla: <c>NpcLook.Parse</c> la da por
    /// inválida y el retrato sale vacío, sin un solo error por ninguna parte.
    /// </remarks>
    public class PortraitLookTests
    {
        [Fact]
        public void El_hexadecimal_de_la_base_NO_se_puede_dibujar()
        {
            // Tal cual está en la columna Look de un personaje de verdad.
            const string deLaBase = "0801120CF5B7CB34888CA02892A6C82018032218A28B9B0FCBE5F615A4E1B919";

            Assert.False(NpcLook.Parse(deLaBase).Valid);
        }

        [Fact]
        public void La_de_llaves_si()
        {
            // La que compone el servidor: hueso 1 —el rig humanoide, el de los jugables—, sus
            // pieles, los seis colores y la escala.
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
            // Ocra hembra con la cabeza 137, que es la que tienen los personajes de prueba. El id
            // en cero es "nadie": sin personaje en la base no hay equipo ni cosméticos que añadir,
            // así que lo que queda es exactamente el cuerpo y la cara.
            var quien = new Jondo.Unity.Server.DatabaseManager.DbCharacter
            {
                Id = 0, Name = "prueba", Breed = 9, Sex = 1, Level = 200, HeadId = 137,
            };

            var look = NpcLook.Parse(Jondo.Unity.Server.Managers.BreedLookTable.Drawable(quien));

            Assert.True(look.Valid);
            Assert.True(look.Humanoid);

            // LA PRIMERA PIEL ES EL CUERPO: es de donde sale la raza para elegir el rig. Poniendo
            // cualquier otra cosa delante -- la cabeza, un sombrero -- no se encuentra el rig y no
            // se dibuja nada, sin un solo error.
            var suyo = Jondo.Unity.Server.Managers.BreedLookTable.Get(9, 1);
            Assert.NotNull(suyo);
            Assert.Equal((int)suyo!.Skins[0], look.Skins[0]);
            Assert.Equal(9, Breeds.Of(look.Skins[0]));

            // Y la cabeza va detrás, añadida. Sin ella el personaje sale sin cara.
            int cabeza = Jondo.Unity.Server.Managers.HeadTable.SkinFor(137, 9, 1);
            Assert.True(cabeza > 0, "la cabeza 137 tiene que tener piel en heads.json");
            Assert.Contains(cabeza, look.Skins);
            Assert.NotEqual(cabeza, look.Skins[0]);
        }

        [Fact]
        public void Sin_raza_conocida_no_hay_cadena()
        {
            // Una raza que no existe no puede componer nada, y tiene que devolver vacío en vez de
            // una cadena a medias que el lector daría por válida.
            var quien = new Jondo.Unity.Server.DatabaseManager.DbCharacter
            {
                Id = 0, Name = "prueba", Breed = 999, Sex = 0,
            };

            Assert.Equal("", Jondo.Unity.Server.Managers.BreedLookTable.Drawable(quien));
        }

        [Fact]
        public void Sin_aspecto_no_hay_retrato_y_no_pasa_nada()
        {
            // Una cuenta cuyo personaje el servidor no sepa componer manda cadena vacía. Tiene que
            // quedarse sin retrato, no reventar: la ficha se lee igual con la inicial.
            Assert.False(NpcLook.Parse("").Valid);
            Assert.False(NpcLook.Parse(null).Valid);
        }
    }
}
