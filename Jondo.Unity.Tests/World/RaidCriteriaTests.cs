using System;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// El lenguaje de criterios del propio cliente, evaluado contra las cadenas que el cliente
    /// trae escritas, sin tocarles una coma, y la instancia de raid que las contesta.
    /// </summary>
    public class RaidCriteriaTests
    {
        /// <summary>
        /// La que llevan los ocho monstruos de la Sima del Gigalodón en su
        /// <c>aggressiveImmunityCriterion</c>. Copiada tal cual del volcado del cliente.
        /// </summary>
        private const string MonsterImmunity =
            "(PB=1131&RV!7,n1_worldlight,0)|(PB=1132&RV!7,n2_worldlight,0)|" +
            "(PB=1133&RV!7,n3_worldlight,0)|(PB=1134&RV!7,n4_worldlight,0)|" +
            "(PB=1135&RV!7,n5_worldlight,0)";

        private static RaidInstance Raid() => new(
            id: 1, raidId: Raids.Gigalodon, guildId: 42043, captainId: 7001,
            startedUtc: new DateTimeOffset(2026, 9, 17, 22, 0, 0, TimeSpan.Zero),
            runsFor: TimeSpan.FromHours(1));

        /// <summary>
        /// Con luz, el monstruo es inmune a la agresión; con la luz a cero, deja de serlo y se
        /// echa encima. Es lo que dice su criterio, y es la mecánica de las luminomáquinas.
        /// </summary>
        [Fact]
        public void A_monster_stops_being_peaceful_when_its_floor_goes_dark()
        {
            var raid = Raid();
            raid.Set(RaidInstance.LightVariable(1), 4);

            Assert.True(Criterion.Met(MonsterImmunity, raid.ResolverFor(subArea: 1131)));

            raid.Set(RaidInstance.LightVariable(1), 0);
            Assert.False(Criterion.Met(MonsterImmunity, raid.ResolverFor(subArea: 1131)));
        }

        /// <summary>
        /// Y cada planta lleva la suya: apagar la primera no vuelve agresivos a los de la
        /// segunda, porque el criterio empareja la subárea con su propia variable.
        /// </summary>
        [Fact]
        public void Each_floor_carries_its_own_light()
        {
            var raid = Raid();
            raid.Set(RaidInstance.LightVariable(1), 0);
            raid.Set(RaidInstance.LightVariable(2), 3);

            Assert.False(Criterion.Met(MonsterImmunity, raid.ResolverFor(1131)));
            Assert.True(Criterion.Met(MonsterImmunity, raid.ResolverFor(1132)));

            // Y en un mapa que no es de la raid no hay ninguna rama que se cumpla.
            Assert.False(Criterion.Met(MonsterImmunity, raid.ResolverFor(233)));
        }

        /// <summary>
        /// El cofre de la raid cambia de aspecto por puntuación: son cinco escalones y el
        /// criterio de cada uno sale del propio PNJ 7861.
        /// </summary>
        [Fact]
        public void The_chest_changes_its_look_by_score()
        {
            var raid = Raid();
            string primero = "RV<7,Raid_Score,5000";
            string segundo = "RV>7,Raid_Score,4999&RV<7,Raid_Score,13000";
            string ultimo = "RV>7,Raid_Score,44999";

            Assert.True(Criterion.Met(primero, raid.ResolverFor(1131)));
            Assert.False(Criterion.Met(segundo, raid.ResolverFor(1131)));

            raid.Set(RaidInstance.ScoreVariable, 6000);
            Assert.False(Criterion.Met(primero, raid.ResolverFor(1131)));
            Assert.True(Criterion.Met(segundo, raid.ResolverFor(1131)));
            Assert.False(Criterion.Met(ultimo, raid.ResolverFor(1131)));

            raid.Set(RaidInstance.ScoreVariable, 45000);
            Assert.True(Criterion.Met(ultimo, raid.ResolverFor(1131)));
        }

        /// <summary>
        /// Lo que un criterio NO sabe no se cuenta como que sí ni como que no: el premio de jefe
        /// mira una alteración («HA!885») que esta instancia no conoce, así que la respuesta es
        /// que no se sabe -aunque la parte de la puntuación se cumpla-, y quien pregunta decide.
        /// </summary>
        [Fact]
        public void What_a_raid_does_not_know_comes_back_unknown()
        {
            var raid = Raid();
            raid.Set(RaidInstance.ScoreVariable, 20000);
            string premio = "HA!885&RV>7,Raid_Score,9999";

            Assert.Equal(Answer.Unknown, Criterion.Evaluate(premio, raid.ResolverFor(1135)));
            Assert.False(Criterion.Met(premio, raid.ResolverFor(1135)));
            Assert.True(Criterion.Met(premio, raid.ResolverFor(1135), unknownCounts: true));

            // Pero un «no» conocido cierra la puerta aunque lo otro sea un misterio.
            raid.Set(RaidInstance.ScoreVariable, 10);
            Assert.Equal(Answer.False, Criterion.Evaluate(premio, raid.ResolverFor(1135)));
        }

        /// <summary>
        /// La gramática, sobre criterios de fuera de la raid: un criterio vacío se cumple, la
        /// «o» se queda con una rama y los paréntesis mandan.
        /// </summary>
        [Fact]
        public void The_grammar_is_the_clients_own()
        {
            var raid = Raid();
            raid.Set("uno", 1);
            Criterion.Resolver resolver = c => c.Code == "RV" && c.Args.Count == 3 && c.Args[0] == "7"
                ? Criterion.Compare(c.Operator, raid.Get(c.Args[1]), c.Value)
                : Answer.Unknown;

            Assert.True(Criterion.Met("", resolver));
            Assert.True(Criterion.Met("RV=7,uno,1", resolver));
            Assert.True(Criterion.Met("RV=7,uno,9|RV=7,uno,1", resolver));
            Assert.False(Criterion.Met("RV=7,uno,9&RV=7,uno,1", resolver));
            Assert.True(Criterion.Met("(RV=7,uno,9|RV=7,uno,1)&RV<7,uno,5", resolver));
            Assert.False(Criterion.Met("RV=7,uno,9|(RV=7,uno,1&RV>7,uno,5)", resolver));
        }

        /// <summary>Las dos raids y sus plantas, que son las subáreas del cliente.</summary>
        [Fact]
        public void The_two_raids_are_the_areas_of_the_client()
        {
            var sima = Raids.Of(Raids.Gigalodon);
            Assert.Equal(103, sima.Area);
            Assert.Equal(6, sima.Floors.Count);
            Assert.Equal(1131, sima.Floors[0]);
            Assert.Equal(1136, sima.Floors[5]);
            Assert.Equal(4, sima.FloorOf(1134));
            Assert.True(sima.HasLight);

            var jardines = Raids.Of(Raids.EternalGardens);
            Assert.Equal(102, jardines.Area);
            Assert.Equal(5, jardines.Floors.Count);
            Assert.False(jardines.HasLight);

            Assert.Equal(Raids.Gigalodon, Raids.BySubArea(1133).Id);
            Assert.Equal(Raids.EternalGardens, Raids.BySubArea(1128).Id);
            Assert.Null(Raids.BySubArea(233));
        }

        /// <summary>
        /// La instancia: quién está dentro, cuánto queda y cómo se acaba. Una raid no se acaba
        /// dos veces, y el reloj se para donde se acabó.
        /// </summary>
        [Fact]
        public void A_raid_runs_for_its_hour_and_finishes_once()
        {
            var raid = Raid();
            var empezo = raid.StartedUtc;

            Assert.True(raid.Running);
            Assert.Equal(TimeSpan.FromHours(1), raid.Left(empezo));
            Assert.Equal(TimeSpan.FromMinutes(20), raid.Left(empezo.AddMinutes(40)));
            Assert.Equal(TimeSpan.Zero, raid.Left(empezo.AddHours(2)));

            Assert.True(raid.Add(7001));
            Assert.False(raid.Add(7001));
            Assert.True(raid.Add(7002));
            Assert.Equal(2, raid.Members.Count);
            Assert.True(raid.Has(7002));
            Assert.True(raid.Remove(7002));
            Assert.False(raid.Has(7002));

            raid.Finish(RaidInstance.Ending.Captain, empezo.AddMinutes(10));
            Assert.False(raid.Running);
            Assert.Equal(RaidInstance.Ending.Captain, raid.Over);
            Assert.Equal(TimeSpan.Zero, raid.Left(empezo.AddMinutes(10)));

            raid.Finish(RaidInstance.Ending.Beaten, empezo.AddMinutes(20));
            Assert.Equal(RaidInstance.Ending.Captain, raid.Over);
        }

        /// <summary>La puntuación se suma, que es lo que hace el cofre al depositar tesoros.</summary>
        [Fact]
        public void The_score_adds_up()
        {
            var raid = Raid();
            Assert.Equal(0, raid.Score);
            Assert.Equal(1000, raid.Add(RaidInstance.ScoreVariable, 1000));
            Assert.Equal(6000, raid.Add(RaidInstance.ScoreVariable, 5000));
            Assert.Equal(6000, raid.Score);
            Assert.Equal(RaidInstance.Namespace, 7);
        }
    }
}
