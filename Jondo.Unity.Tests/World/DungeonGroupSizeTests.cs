using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// El grupo de una sala de mazmorra iguala en número a quienes van a pelear con él.
    /// </summary>
    /// <remarks>
    /// Entras solo y en la sala del jefe son cuatro; entráis siete y son siete. Aquí el grupo era
    /// fijo, así que un equipo de siete se peleaba contra los tres de siempre.
    ///
    /// El ajuste se hace al pisar la sala y no al empezar el combate, porque el grupo se DIBUJA
    /// antes de que el combate exista: verlo de tres y pelear contra siete sería peor que no
    /// ajustarlo.
    ///
    /// Estos tests son sobre la regla, no sobre el mapa: sin mazmorras cargadas la llamada no toca
    /// nada y devuelve cero, que es lo que se comprueba abajo. Que el grupo correcto aparezca en la
    /// sala correcta es cosa del servidor y de alguien con un cliente, y no se afirma aquí.
    /// </remarks>
    [Collection("MapManager")]
    public class DungeonGroupSizeTests
    {
        [Fact]
        public void Un_mapa_que_no_es_sala_no_se_toca()
        {
            // La guarda que importa: esto corre al pisar CUALQUIER mapa, así que lo primero que
            // tiene que hacer bien es no hacer nada en los 15.360 que no son mazmorra.
            Assert.Equal(0, MobSpawnManager.SizeRoomToParty(153358340, 4));
        }

        [Fact]
        public void Sin_atacantes_tampoco()
        {
            // Cero atacantes no es «vacía la sala»: es que no se sabe cuántos son, y ante eso lo
            // correcto es dejar el grupo como estaba.
            Assert.Equal(0, MobSpawnManager.SizeRoomToParty(190449664, 0));
            Assert.Equal(0, MobSpawnManager.SizeRoomToParty(190449664, -3));
        }

        [Fact]
        public void La_sala_del_jefe_tiene_un_minimo_y_esta_dicho()
        {
            // Cuatro: el jefe y tres más. Un jefe solo en una sala vacía no es el final de nada y
            // además se le mata de un turno. Con más atacantes sube; por debajo, no baja.
            Assert.Equal(4, MobSpawnManager.MinimoEnLaSalaDelJefe);
        }
    }
}
