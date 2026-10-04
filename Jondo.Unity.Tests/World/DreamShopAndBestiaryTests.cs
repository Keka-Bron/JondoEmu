using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The fountain's shop (the f6 of the izg) and the room's bestiary (the f3), against the
    /// bytes of the long capture's fountain and of the Paradoja III capture's first room.
    /// </summary>
    [Collection("MapManager")]
    public class DreamShopAndBestiaryTests
    {
        private static Dreams.Sueno New(int difficulty = 4)
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(1, "Prueba", 200, difficulty, 100, 200);
            Dreams.Enter(dream, 0, out _);
            return dream;
        }

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        /// <summary>The five offers of the long capture's fountain, byte for byte and in their order.</summary>
        [Fact]
        public void The_shop_is_the_bytes_of_the_capture()
        {
            string[] measured =
            {
                "080010001a090a052014589b1710011a090a052028589c16100120002800400f48ce7350415800",
                "080010001a0b0a0720ef990558cd1a1001200028003802400f489d785095015800",
                "080010001a090a05206458a21610011a090a05206458a416100120002800400f48cf7350675800",
                "080010001a0b0a0720e58d0558cd1a1001200028003802400f48967850595800",
                "080010001a080a04201958731001200028003802400f48dd73507c5800",
            };
            Assert.Equal(measured, Dreams.ShopOffers.Select(o => Convert.ToHexString(DreamProtocol.BuildReward(o)).ToLowerInvariant()));
        }

        /// <summary>
        /// At a fountain the izg carries what the shop has left, one f6 each; buying takes the
        /// price off the dream points, adds the bonuses and takes the offer out.
        /// </summary>
        [Fact]
        public void The_fountain_sells_what_it_has_for_dream_points()
        {
            var dream = New();
            var fountain = dream.Salas.First(r => r.EsFuente);
            Dreams.Enter(dream, fountain.Id, out _);
            Assert.Equal(5, Fields(DreamProtocol.BuildDreamState(dream)).Count(f => f.FieldNumber == 6));

            // Five dream points for a price of fifteen: nothing.
            dream.DreamPoints = 5;
            Assert.Null(Dreams.Buy(dream, 14813, out string refusal));
            Assert.NotEmpty(refusal);

            // Twenty: the critical offer, by its reward id.
            dream.DreamPoints = 20;
            var bought = Dreams.Buy(dream, 14813, out _);
            Assert.NotNull(bought);
            Assert.Equal(5, dream.DreamPoints);
            Assert.Contains(dream.Ganados, b => b.Efecto == 115 && b.Valor == 25);
            Assert.Equal(4, Fields(DreamProtocol.BuildDreamState(dream)).Count(f => f.FieldNumber == 6));

            // And by its place in the shop: the first one left, with its two bonuses.
            dream.DreamPoints = 15;
            Assert.Equal(14798, Dreams.Buy(dream, 0, out _)!.Id);
            Assert.Equal(0, dream.DreamPoints);
            Assert.Equal(3, dream.Salas.First(r => r.EsFuente).Offers!.Count);
        }

        /// <summary>
        /// The client buys by the offer's f10: clicking "Psst Psst" sent iym { f1: 149 }, the f10
        /// of reward 15389, and nothing was bought while the server looked for a reward id.
        /// </summary>
        [Fact]
        public void The_client_buys_by_the_f10()
        {
            var dream = New();
            var fountain = dream.Salas.First(r => r.EsFuente);
            Dreams.Enter(dream, fountain.Id, out _);
            dream.DreamPoints = 35;

            var bought = Dreams.Buy(dream, 149, out _);
            Assert.NotNull(bought);
            Assert.Equal(15389, bought!.Id);
            Assert.Equal(20, dream.DreamPoints);
            Assert.Contains(dream.Ganados, b => b.Efecto == 3405 && b.Valor == 85231);
        }

        /// <summary>Anywhere else there is no shop: no f6, and nothing to buy.</summary>
        [Fact]
        public void Only_a_fountain_sells()
        {
            var dream = New();
            dream.DreamPoints = 100;
            Assert.DoesNotContain(Fields(DreamProtocol.BuildDreamState(dream)), f => f.FieldNumber == 6);
            Assert.Null(Dreams.Buy(dream, 0, out _));
        }

        /// <summary>
        /// A monster of the bestiary, against the first of the Paradoja III capture's room 1:
        /// monster 1153 on cell 283, level 200, and its thirteen characteristics.
        /// </summary>
        [Fact]
        public void A_beast_is_the_bytes_of_the_capture()
        {
            var beast = new DreamProtocol.Beast(283, 1153, 200, false, new List<(int, int)>
            {
                (0, 5450), (1, 10), (33, 17), (34, 10), (35, 41), (36, -33), (37, 15),
                (44, 3263), (78, 88), (79, 88), (23, 4), (27, 69), (28, 69),
            });
            Assert.Equal("089b0210810918c8012a05080010ca2a2a040801100a2a04082110112a040822100a2a04082310292a0d0824" +
                         "10dfffffffffffffffff012a040825100f2a05082c10bf192a04084e10582a04084f10582a04081710042a04" +
                         "081b10452a04081c1045",
                         Convert.ToHexString(DreamProtocol.BuildBeast(beast)).ToLowerInvariant());

            // A boss carries f4, as monster 8099 does after the storm of the same capture.
            var boss = new DreamProtocol.Beast(205, 8099, 220, true, new List<(int, int)>());
            Assert.Equal("08cd0110a33f18dc012001", Convert.ToHexString(DreamProtocol.BuildBeast(boss)).ToLowerInvariant());
        }

        /// <summary>The value-4 bit of m_flags: set on 8099 and 173, which the bestiary marks, not on 209.</summary>
        [Fact]
        public void The_boss_flag_is_the_template_s()
        {
            Assert.True(Dreams.IsBoss(8099));
            Assert.True(Dreams.IsBoss(173));
            Assert.False(Dreams.IsBoss(209));
        }

        /// <summary>
        /// The bestiary of a room is its group as the fight builds it, on the cells the fight
        /// places it on, and it goes in the izg only while the fight is to be won.
        /// </summary>
        [Fact]
        public void The_bestiary_is_the_fight_to_come()
        {
            MobSpawnManager.EnsureMonsterData();
            var dream = New();
            var room = dream.Salas.First(r => r.Miembros.Count > 0 && dream.Buscar(0)!.Salidas.Contains(r.Id));
            Dreams.Enter(dream, room.Id, out _);

            var beasts = DreamHandler.BestiaryOf(room);
            Assert.Equal(room.Miembros.Count, beasts.Count);
            var cells = FightHandler.DefenderPlacement(room.MapaDeLaSala);
            for (int i = 0; i < beasts.Count; i++)
            {
                Assert.Equal(i < cells.Count ? cells[i] : cells[0], beasts[i].Cell);
                Assert.Equal(room.Miembros[i].Monstruo, beasts[i].MonsterId);
                Assert.Equal(0, beasts[i].Stats[0].Characteristic);
                Assert.True(beasts[i].Stats[0].Value > 0);
            }

            Assert.Equal(beasts.Count, Fields(DreamProtocol.BuildDreamState(dream, beasts)).Count(f => f.FieldNumber == 3));

            room.Hecha = true;
            Assert.Empty(DreamHandler.BestiaryOf(room));
            Assert.DoesNotContain(Fields(DreamProtocol.BuildDreamState(dream, beasts)), f => f.FieldNumber == 3);
        }
    }
}
