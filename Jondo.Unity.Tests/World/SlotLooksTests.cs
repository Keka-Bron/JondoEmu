using System.Collections.Generic;
using System.Linq;
using Jondo.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The bonus slots that change how the map draws a character: the disguises of the roleplay
    /// buffs and curses (149), and the followers (148).
    /// </summary>
    public class SlotLooksTests
    {
        private static SlotLooks.Worn Worn(int position, params Equipment.ItemEffect[] effects)
            => new SlotLooks.Worn(position * 1000L, position, effects);

        [Fact]
        public void The_bonus_slots_take_curses_roleplay_buffs_and_followers()
        {
            Assert.Equal(new[] { 24, 25 }, BonusSlots.SlotsFor(BonusSlots.CurseType));
            Assert.Equal(new[] { 26 }, BonusSlots.SlotsFor(BonusSlots.RoleplayBuffType));
            Assert.Equal(new[] { 27 }, BonusSlots.SlotsFor(BonusSlots.FollowerType));
        }

        private static bool Recovered()
            => System.IO.File.Exists(Jondo.Unity.Launcher.Paths.ContentFile(Cosmetics.LostAppearancesFile));

        /// <summary>
        /// A disguise's 149 is an appearance, not bones: bones 1039 are a ghost dragoturkey, appearance
        /// 1039 the bworky «Nativo del signo Bworka» promises.
        /// </summary>
        [Fact]
        public void A_disguise_is_its_appearance_and_the_roleplay_buffer_s_comes_first()
        {
            if (!Recovered()) return;

            // «Camuflaje» (2062): 149 884, the stealth bones with no graphics; «Más Bonita que
            // Ninguna» (10043), a curse: 149 875, an appearance still unknown.
            var camouflage = Worn(26, new Equipment.ItemEffect(149, 884, 0, 0));
            var curse = Worn(24, new Equipment.ItemEffect(149, 875, 0, 0), new Equipment.ItemEffect(811, 1, 1, 1));
            var bworky = Worn(26, new Equipment.ItemEffect(811, 1, 1, 1), new Equipment.ItemEffect(149, 1039, 0, 0));

            Assert.Equal("{761}", SlotLooks.DisguiseLook(new[] { curse, camouflage }));
            Assert.Equal("{264|||80}", SlotLooks.DisguiseLook(new[] { bworky }));
            Assert.Null(SlotLooks.DisguiseLook(new[] { curse }));
            Assert.Null(SlotLooks.DisguiseLook(new[] { Worn(22, new Equipment.ItemEffect(111, 1, 0, 0)) }));
        }

        /// <summary>A follower item keeps what it is: 148's first number is lost unless it travels whole.</summary>
        [Fact]
        public void A_follower_item_keeps_its_kind()
        {
            if (!System.IO.File.Exists(Jondo.Unity.Launcher.Paths.EffectFieldsJson)) return;
            using var fields = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(Jondo.Unity.Launcher.Paths.EffectFieldsJson));
            Assert.Equal(EffectFields.AsDice, fields.RootElement.GetProperty("148").GetInt32());
        }

        [Fact]
        public void A_follower_is_an_npc_a_monster_or_an_appearance()
        {
            if (!Recovered()) return;

            // «Perkutador» (34223): 148 with 4 and appearance 3413, the perkutador's look; the
            // «Globo paseador mágico»'s 1036 is an appearance still unknown and walks behind no one.
            var perkutador = Worn(27, new Equipment.ItemEffect(148, 3413, 4, 0));
            var balloon = Worn(27, new Equipment.ItemEffect(148, 1036, 4, 0));
            Assert.Equal(new[] { "{1328|||100}" }, SlotLooks.FollowerLooks(new[] { perkutador }));
            Assert.Empty(SlotLooks.FollowerLooks(new[] { balloon }));

            Assert.Null(SlotLooks.LookOfFollower(3, 3413));
            Assert.Null(SlotLooks.LookOfFollower(SlotLooks.FollowerAppearance, 0));
        }

        [Fact]
        public void The_map_draws_the_disguise_and_the_followers()
        {
            if (!Recovered()) return;

            var session = GameSession.SinSocket();
            session.State.CharacterId = 9_400_200_001;
            var camouflage = new Equipment.Item { Uid = 9_400_200_101, Template = 2062, Position = 26 };
            camouflage.Effects.Add(new Equipment.ItemEffect(149, 884, 0, 0));
            var follower = new Equipment.Item { Uid = 9_400_200_102, Template = 34223, Position = 27 };
            follower.Effects.Add(new Equipment.ItemEffect(148, 3413, 4, 0));
            follower.Effects.Add(new Equipment.ItemEffect(811, 1, 1, 1));
            session.State.EquipmentItems[camouflage.Uid] = camouflage;
            session.State.EquipmentItems[follower.Uid] = follower;

            using (SessionContext.Push(session))
            {
                var character = new DatabaseManager.DbCharacter
                {
                    Id = session.State.CharacterId, Name = "Arbusto", Level = 50, Breed = 1, Sex = 0,
                };

                // The look: the camouflage's invisible bones (its f3).
                Assert.Equal(761L, Varint(ProtoMessage.Parse(BreedLookTable.MapLook(character)), 3));

                // The actor: f2 { f1 { f5 humanoid { f3 body { f5 options } } } } with f6 the followers.
                var actor = ProtoMessage.Parse(ConnectionProtocol.BuildPlayerActorBlock(character, 300, 1, 1));
                var body = Sub(Sub(Sub(Sub(actor, 2), 1), 5), 3);
                var followers = body.Fields.Where(f => f.FieldNumber == 5 && f.WireType == 2)
                                           .Select(f => ProtoMessage.Parse(f.BytesValue))
                                           .Select(option => option.Fields.FirstOrDefault(f => f.FieldNumber == 6))
                                           .Single(f => f != null)!;
                var walking = Sub(ProtoMessage.Parse(followers.BytesValue), 1);
                Assert.Equal(1328L, Varint(Sub(walking, 1), 3));
            }
        }

        private static ProtoMessage Sub(ProtoMessage message, int field)
            => ProtoMessage.Parse(message.Fields.First(f => f.FieldNumber == field && f.WireType == 2).BytesValue);

        private static long Varint(ProtoMessage message, int field)
            => message.Fields.First(f => f.FieldNumber == field && f.WireType == 0).VarIntValue;
    }
}
