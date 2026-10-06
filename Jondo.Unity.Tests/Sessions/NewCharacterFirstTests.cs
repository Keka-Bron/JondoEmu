using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using DbCharacter = Jondo.Unity.Server.DatabaseManager.DbCharacter;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// The freshly created character goes FIRST in the list sent afterwards.
    /// </summary>
    /// <remarks>
    /// The client does not ask which one you want: it takes the first of the list and sends its selection
    /// instantly. In the log it is seen with five milliseconds of difference:
    ///
    ///   00:16:05.798  Creado Tymaviejas (id 13825564)
    ///   00:16:05.803  Selected character 13825558     <- the previous one
    ///
    /// Our list comes from an «ORDER BY Id», so the freshly created one —the highest id— went
    /// last and the client entered the world with the old one. One had to go back to the selection
    /// screen and pick it by hand.
    ///
    /// That the new one goes in front is measured in «crear personaje - borrar personaje»: the kvi that
    /// follows the kvb carries «Vos-Xx», the one just created, ahead of «Berru».
    /// </remarks>
    public class NewCharacterFirstTests
    {
        /// <summary>The same the handler does when sending the list after creating.</summary>
        private static List<DbCharacter> ConElNuevoDelante(
            List<DbCharacter> lista, long recienCreado)
        {
            var nuevo = lista.Find(c => c.Id == recienCreado);
            if (nuevo != null)
            {
                lista.Remove(nuevo);
                lista.Insert(0, nuevo);
            }
            return lista;
        }

        private static List<DbCharacter> Lista(params long[] ids)
            => ids.Select(i => new DbCharacter { Id = i, Name = "P" + i }).ToList();

        [Fact]
        public void El_recien_creado_encabeza_la_lista()
        {
            // The ids come sorted from the base; the new one is the highest and without this it went last.
            var lista = ConElNuevoDelante(Lista(13825558, 13825560, 13825564), 13825564);

            Assert.Equal(13825564, lista[0].Id);
            Assert.Equal(3, lista.Count);
        }

        [Fact]
        public void Los_demas_conservan_su_orden_relativo()
        {
            var lista = ConElNuevoDelante(Lista(100, 200, 300, 400), 300);

            Assert.Equal(new long[] { 300, 100, 200, 400 }, lista.Select(c => c.Id).ToArray());
        }

        [Fact]
        public void Si_el_nuevo_no_esta_la_lista_se_queda_como_estaba()
        {
            // It must not break nor reorder on its own: better an intact list than one
            // shuffled by an id that does not exist.
            var lista = ConElNuevoDelante(Lista(100, 200), 999);

            Assert.Equal(new long[] { 100, 200 }, lista.Select(c => c.Id).ToArray());
        }
    }
}
