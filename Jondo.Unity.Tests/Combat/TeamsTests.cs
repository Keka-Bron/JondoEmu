using System.Linq;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Los dos bandos de un combate, y las preguntas que el motor les hace.
    /// </summary>
    /// <remarks>
    /// Se llamaban <c>Team0</c> y <c>Team1</c>, con «// Players» y «// Monsters» al lado. Ciento
    /// cinco referencias más adelante eso había dejado de ser un comentario y era una creencia:
    /// medio motor daba por hecho que en el azul está quien juega y enfrente hay bichos.
    ///
    /// Contra monstruos es verdad. En un desafío es verdad para UNO de los dos, y de ahí salió una
    /// clase entera de fallos que no llevaban ningún «if» porque nadie sabía que eran supuestos.
    /// Esto sujeta las preguntas que los sustituyen.
    /// </remarks>
    public class TeamsTests
    {
        /// <summary>Un desafío: una persona en cada lado.</summary>
        private static FightInstance UnDesafio()
        {
            var fight = new FightInstance(1, 100, 200) { Reglas = FightRules.Desafio };
            fight.GeneratePlacementCells(Enumerable.Range(200, 40).ToList());
            fight.AddPlayer(new Fighter { Id = 10, MaxHP = 500, CurrentHP = 500 });
            fight.AddOpponent(new Fighter { Id = 20, MaxHP = 500, CurrentHP = 500 });
            return fight;
        }

        // ───────────────────────────────────────────── quién está en qué lado

        [Fact]
        public void Cada_uno_sabe_de_que_bando_es()
        {
            var fight = UnDesafio();

            Assert.Equal(FightInstance.Azules, fight.EquipoDe(10));
            Assert.Equal(FightInstance.Rojos, fight.EquipoDe(20));

            // Y quien no está en el combate no es de ningún bando, que no es lo mismo que ser azul.
            Assert.Equal(-1, fight.EquipoDe(999));
        }

        [Fact]
        public void Buscar_encuentra_en_los_dos_lados()
        {
            var fight = UnDesafio();

            Assert.Equal(10, fight.Buscar(10)?.Id);
            Assert.Equal(20, fight.Buscar(20)?.Id);
            Assert.Null(fight.Buscar(999));
        }

        [Fact]
        public void Los_enemigos_de_uno_son_los_aliados_del_otro()
        {
            var fight = UnDesafio();

            Assert.Equal(new long[] { 20 }, fight.Enemigos(10).Select(f => f.Id));
            Assert.Equal(new long[] { 10 }, fight.Enemigos(20).Select(f => f.Id));
            Assert.Equal(new long[] { 10 }, fight.Aliados(10).Select(f => f.Id));
        }

        [Fact]
        public void Quien_no_esta_no_tiene_ni_aliados_ni_enemigos()
        {
            // Devolver el bando azul por descarte es justo el fallo que esto viene a impedir.
            var fight = UnDesafio();

            Assert.Empty(fight.Aliados(999));
            Assert.Empty(fight.Enemigos(999));
        }

        // ───────────────────────────────────────────── quién ha ganado

        [Fact]
        public void Ganar_depende_del_lado_en_el_que_estuvieras()
        {
            var fight = UnDesafio();
            fight.Buscar(20).CurrentHP = 0;

            Assert.True(fight.HaGanado(10));
            Assert.False(fight.HaGanado(20));

            // «Sigue vivo el azul» es un hecho del combate; «he ganado» es del que pregunta. Se
            // escribían con el mismo booleano y por eso el perdedor de un desafío recibía la lista
            // de resultados con los bandos cambiados.
            Assert.True(fight.SigueVivo(FightInstance.Azules));
            Assert.False(fight.SigueVivo(FightInstance.Rojos));
        }

        [Fact]
        public void El_que_no_estaba_no_ha_ganado()
        {
            Assert.False(UnDesafio().HaGanado(999));
        }

        // ───────────────────────────────────────────── el «listo»

        [Fact]
        public void El_combate_no_empieza_hasta_que_los_dos_estan_listos()
        {
            // Esto miraba SOLO el azul, en sus dos mitades: el combate arrancaba en cuanto pulsaba
            // listo el retador —su bando estaba entero listo porque era él solo— y el «listo» del
            // retado no se apuntaba en ninguna parte. Es lo que se veía como «uno ya está peleando
            // y el otro sigue en colocación».
            var fight = UnDesafio();

            Assert.False(fight.SetFighterReady(10));
            Assert.Equal(FightState.Placement, fight.State);

            Assert.True(fight.SetFighterReady(20));
            Assert.Equal(FightState.Ongoing, fight.State);
        }

        [Fact]
        public void Contra_monstruos_basta_con_que_pulse_el_jugador()
        {
            // Un bicho no pulsa nada, así que no cuenta para esperar. Sin esta parte, el arreglo
            // de arriba dejaría todos los combates contra monstruos sin empezar jamás.
            var fight = new FightInstance(2, 100, 200);
            fight.GeneratePlacementCells(Enumerable.Range(200, 40).ToList());
            fight.AddPlayer(new Fighter { Id = 10, MaxHP = 500, CurrentHP = 500 });
            fight.AddMonster(new Fighter { Id = -1, MaxHP = 100, CurrentHP = 100, IsMonster = true });

            Assert.True(fight.SetFighterReady(10));
            Assert.Equal(FightState.Ongoing, fight.State);
        }

        // ───────────────────────────────────────────── colocarse

        [Fact]
        public void Cada_uno_se_coloca_en_las_casillas_de_su_lado()
        {
            var fight = UnDesafio();
            int azulLibre = fight.BluePlacementCells.First(c => c != fight.Buscar(10).CellId);
            int rojaLibre = fight.RedPlacementCells.First(c => c != fight.Buscar(20).CellId);

            fight.ChangePlacementCell(10, azulLibre);
            fight.ChangePlacementCell(20, rojaLibre);

            Assert.Equal(azulLibre, fight.Buscar(10).CellId);
            Assert.Equal(rojaLibre, fight.Buscar(20).CellId);
        }

        [Fact]
        public void Nadie_se_coloca_en_las_casillas_del_otro()
        {
            // Se comprobaba siempre contra las azules, que contra monstruos da igual porque en el
            // azul sólo hay una persona. En un desafío dejaba al retado sin poder recolocarse.
            var fight = UnDesafio();
            int suya = fight.Buscar(20).CellId;

            fight.ChangePlacementCell(20, fight.BluePlacementCells[0]);

            Assert.Equal(suya, fight.Buscar(20).CellId);
        }
    }
}
