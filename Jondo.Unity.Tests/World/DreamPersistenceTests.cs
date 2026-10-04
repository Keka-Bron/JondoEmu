using System;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A dream outlives a disconnection and a restart, the well offers the player's own to
    /// continue, and the astral storm changes the room's group rather than the room.
    /// </summary>
    [Collection("MapManager")]
    public class DreamPersistenceTests
    {
        private static Dreams.Sueno New(int difficulty = 4)
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(1, "Prueba", 200, difficulty, 100, 200, breed: 9);
            Dreams.Enter(dream, 0, out _);
            return dream;
        }

        /// <summary>What goes into the base comes back whole: rooms, graph, rewards, bonuses, path, shop.</summary>
        [Fact]
        public void A_dream_comes_back_from_the_base_whole()
        {
            var dream = New();
            var room = dream.Salas.First(r => r.Miembros.Count > 0 && dream.Buscar(0)!.Salidas.Contains(r.Id));
            Dreams.Enter(dream, room.Id, out _);
            room.Hecha = true;
            var fountain = dream.Salas.First(r => r.EsFuente);
            Dreams.Enter(dream, fountain.Id, out _);
            dream.DreamPoints = 100;
            Assert.NotNull(Dreams.Buy(dream, 0, out _));
            dream.Tormentas = 0;

            var back = Dreams.Deserialize(Dreams.Serialize(dream))!;

            Assert.Equal(dream.CharacterId, back.CharacterId);
            Assert.Equal(dream.Dificultad, back.Dificultad);
            Assert.Equal(dream.Actual, back.Actual);
            Assert.Equal(dream.DreamPoints, back.DreamPoints);
            Assert.Equal(dream.Breed, back.Breed);
            Assert.Equal(0, back.Tormentas);
            Assert.Equal(dream.Visited, back.Visited);
            Assert.Equal(dream.Ganados.Select(b => (b.Efecto, b.Valor, b.Anidado)), back.Ganados.Select(b => (b.Efecto, b.Valor, b.Anidado)));
            Assert.Equal(dream.Salas.Count, back.Salas.Count);

            var again = back.Buscar(room.Id)!;
            Assert.True(again.Hecha);
            Assert.Equal(room.Salidas, again.Salidas);
            Assert.Equal(room.Miembros, again.Miembros);
            Assert.Equal(room.MapaDeLaSala, again.MapaDeLaSala);
            Assert.Equal(room.Reward!.Id, again.Reward!.Id);
            Assert.Equal(4, back.Buscar(fountain.Id)!.Offers!.Count);

            // And it says the same to the client.
            Assert.Equal(DreamProtocol.BuildDreamState(dream), DreamProtocol.BuildDreamState(back));
        }

        /// <summary>No dream going: the well sends nothing to continue, as the long capture's zero bytes.</summary>
        [Fact]
        public void With_no_dream_the_well_offers_none() => Assert.Empty(DreamProtocol.BuildDreamMap(null));

        /// <summary>
        /// The well's header is the saved dream, against the "continuar" capture's: 10 dream
        /// points, +20% vitality, level 200, Paradoja IV, in room "2".
        /// </summary>
        [Fact]
        public void The_well_offers_the_player_s_dream()
        {
            var dream = New(difficulty: 7);
            var named = new Dreams.Sueno { CharacterId = 1, Nombre = "Sacri-Master", Nivel = 200, Dificultad = 7 };
            foreach (var s in dream.Salas) named.Salas.Add(s);
            named.Ganados.Add(new Dreams.Bono(2844, 20));
            named.DreamPoints = 10;
            named.Tormentas = 0;
            named.Actual = 2;

            byte[] iyj = DreamProtocol.BuildDreamMap(named);
            var header = ProtoMessage.Parse(iyj).Fields.Single(f => f.FieldNumber == 1).BytesValue;
            string hex = Convert.ToHexString(header).ToLowerInvariant();
            Assert.StartsWith("100a1a0c53616372692d4d61737465723a090a052014589c16100140c80168077001780182010132", hex);
        }

        /// <summary>
        /// The storm keeps the room and changes its group and its map, as the Paradoja II capture's
        /// two do; the room's reward stays what the door promised.
        /// </summary>
        [Fact]
        public void The_storm_changes_the_group_not_the_room()
        {
            var dream = New();
            var room = dream.Salas.First(r => r.Miembros.Count > 0);
            long map = room.MapaDeLaSala;
            var reward = room.Reward;

            Dreams.Reroll(dream, room);

            Assert.NotEmpty(room.Miembros);
            Assert.NotEqual(map, room.MapaDeLaSala);
            Assert.Same(reward, room.Reward);
            Assert.True(Dreams.IsDreamMap(room.MapaDeLaSala));
        }

        /// <summary>The entrance, a fight room and a fountain are the dream's; the Plano Astral is not.</summary>
        [Fact]
        public void The_dream_knows_its_maps()
        {
            var dream = New();
            Assert.True(Dreams.IsDreamMap(Dreams.MapaDeEntrada));
            Assert.True(Dreams.IsDreamMap(dream.Salas.First(r => r.EsFuente).MapaDeLaSala));
            Assert.False(Dreams.IsDreamMap(DreamHandler.PlanoAstral));
        }
    }
}
