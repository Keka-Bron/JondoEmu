using System.Linq;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The two sides of a fight, and the questions the engine asks them.
    /// </summary>
    /// <remarks>
    /// They were called <c>Team0</c> and <c>Team1</c>, with «// Players» and «// Monsters» next to them. A hundred
    /// and five references later that had stopped being a comment and was a belief:
    /// half the engine took for granted that the one playing is on blue and opposite there are creatures.
    ///
    /// Against monsters it is true. In a challenge it is true for ONE of the two, and from there came a
    /// whole class of bugs that carried no «if» because nobody knew they were assumptions.
    /// This holds the questions that replace them.
    /// </remarks>
    public class TeamsTests
    {
        /// <summary>A challenge: one person on each side.</summary>
        private static FightInstance UnDesafio()
        {
            var fight = new FightInstance(1, 100, 200) { Reglas = FightRules.Desafio };
            fight.GeneratePlacementCells(Enumerable.Range(200, 40).ToList());
            fight.AddPlayer(new Fighter { Id = 10, MaxHP = 500, CurrentHP = 500 });
            fight.AddOpponent(new Fighter { Id = 20, MaxHP = 500, CurrentHP = 500 });
            return fight;
        }

        // ───────────────────────────────────────────── who is on which side

        [Fact]
        public void Cada_uno_sabe_de_que_bando_es()
        {
            var fight = UnDesafio();

            Assert.Equal(FightInstance.Azules, fight.EquipoDe(10));
            Assert.Equal(FightInstance.Rojos, fight.EquipoDe(20));

            // And whoever is not in the fight belongs to no side, which is not the same as being blue.
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
            // Returning the blue side by elimination is exactly the bug this is here to prevent.
            var fight = UnDesafio();

            Assert.Empty(fight.Aliados(999));
            Assert.Empty(fight.Enemigos(999));
        }

        // ───────────────────────────────────────────── who has won

        [Fact]
        public void Ganar_depende_del_lado_en_el_que_estuvieras()
        {
            var fight = UnDesafio();
            fight.Buscar(20).CurrentHP = 0;

            Assert.True(fight.HaGanado(10));
            Assert.False(fight.HaGanado(20));

            // «Blue is still alive» is a fact of the fight; «I have won» belongs to whoever asks. They
            // were written with the same boolean and that is why the loser of a challenge received the list
            // of results with the sides swapped.
            Assert.True(fight.SigueVivo(FightInstance.Azules));
            Assert.False(fight.SigueVivo(FightInstance.Rojos));
        }

        [Fact]
        public void El_que_no_estaba_no_ha_ganado()
        {
            Assert.False(UnDesafio().HaGanado(999));
        }

        // ───────────────────────────────────────────── the «ready»

        [Fact]
        public void El_combate_no_empieza_hasta_que_los_dos_estan_listos()
        {
            // This looked ONLY at blue, in both its halves: the fight started as soon as the challenger
            // pressed ready —his side was all ready because it was just him— and the challenged's
            // «ready» was recorded nowhere. It is what was seen as «one is already fighting
            // and the other is still in placement».
            var fight = UnDesafio();

            Assert.False(fight.SetFighterReady(10));
            Assert.Equal(FightState.Placement, fight.State);

            Assert.True(fight.SetFighterReady(20));
            Assert.Equal(FightState.Ongoing, fight.State);
        }

        [Fact]
        public void Contra_monstruos_basta_con_que_pulse_el_jugador()
        {
            // A creature presses nothing, so it does not count for waiting. Without this part, the fix
            // above would leave every fight against monsters never starting.
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
            // It was always checked against the blue ones, which against monsters does not matter because on
            // blue there is only one person. In a challenge it left the challenged unable to reposition.
            var fight = UnDesafio();
            int suya = fight.Buscar(20).CellId;

            fight.ChangePlacementCell(20, fight.BluePlacementCells[0]);

            Assert.Equal(suya, fight.Buscar(20).CellId);
        }
    }
}
