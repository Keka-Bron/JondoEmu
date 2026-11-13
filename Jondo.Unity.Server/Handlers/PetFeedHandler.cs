using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Feeding a pet from the client's «Alimentar» window (FeedUI): the resources given are spent,
    /// the pet gains their experience, and its level and characteristics grow (Pets).
    /// </summary>
    /// <remarks>
    /// THE WIRE, read off the client (FeedUI.OnConfirmFeed, efm.xgk): itw { f1 the pet's uid,
    /// f2 each food { f2 its uid, f3 how many } }. No capture has it; the answer is the pet sent
    /// again (iuj, as a legendary pet's food answers), each food's stack as it is left (ivj or ium),
    /// and, for a worn pet that grew, the sheet (kub).
    /// What is refused is let go, as the client refuses it first: a legendary pet («errorOnlyInfuse»),
    /// one at level 100 («errorMaximumExperienceReached»), what is no food («errorResourceIsNotFood»)
    /// and more than fifty («errorMaximumResourcesCountReached»).
    /// </remarks>
    public static class PetFeedHandler
    {
        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        /// <summary>The pet and the foods of an itw.</summary>
        public static (long Pet, List<(long Uid, int Quantity)> Meals) Read(byte[] itw)
        {
            long pet = 0;
            var meals = new List<(long, int)>();
            foreach (var field in ProtoMessage.Parse(itw).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) pet = (long)field.VarIntValue;
                else if (field.FieldNumber == 2 && field.WireType == 2)
                {
                    long uid = 0;
                    int quantity = 0;
                    foreach (var m in ProtoMessage.Parse(field.BytesValue).Fields)
                    {
                        if (m.WireType != 0) continue;
                        if (m.FieldNumber == 2) uid = (long)m.VarIntValue;
                        else if (m.FieldNumber == 3) quantity = (int)m.VarIntValue;
                    }
                    if (uid != 0 && quantity > 0) meals.Add((uid, quantity));
                }
            }
            return (pet, meals);
        }

        public static async Task FeedAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? itw = ConnectionProtocol.ReadPayload(payload, Op.Itw);
            if (itw == null) return;
            var (petUid, meals) = Read(itw);
            string who = SessionContext.State.CharacterName;

            var pet = Equipment.ByUid(petUid);
            if (pet == null || !Pets.IsPet(pet.Template))
            {
                Console.WriteLine($"[Pets] {who} feeds {petUid}, which is no pet of his.");
                return;
            }
            if (LegendaryPets.IsLegendary(pet) || Pets.LevelOf(pet.Effects) >= Pets.TopLevel)
            {
                Console.WriteLine($"[Pets] {who}'s pet {petUid} eats no more: legendary or at level {Pets.TopLevel}.");
                return;
            }

            // The foods: his, in the bag, other than the pet, food at all, fifty at most.
            var foods = new List<(Equipment.Item Item, int Quantity)>();
            double gained = 0;
            foreach (var (uid, quantity) in meals.GroupBy(m => m.Uid).Select(g => (g.Key, g.Sum(m => m.Quantity))))
            {
                var food = Equipment.ByUid(uid);
                if (food == null || uid == petUid || food.Position != EquipmentHandler.Bag || quantity > food.Quantity)
                {
                    Console.WriteLine($"[Pets] {who} feeds {quantity} of {uid}, which he has not.");
                    return;
                }
                double worth = LegendaryPets.FoodOf(food.Template) == null ? Pets.ExperienceOfFood(food.Template, quantity) : 0;
                if (worth <= 0)
                {
                    Console.WriteLine($"[Pets] {who} offers item {food.Template}, which is no food.");
                    return;
                }
                foods.Add((food, quantity));
                gained += worth;
            }
            if (foods.Count == 0) return;
            if (foods.Sum(f => f.Quantity) > Pets.MostPerFeeding)
            {
                Console.WriteLine($"[Pets] {who} offers more than {Pets.MostPerFeeding} foods at once.");
                return;
            }

            // Today's Almanax may make it more (its type 30).
            long experience = Almanax.WithBonus(Almanax.BonusType.PetExperience, (long)Math.Floor(gained));

            long characterId = SessionContext.State.CharacterId;
            foreach (var (food, quantity) in foods)
            {
                int left = food.Quantity - quantity;
                if (!DatabaseManager.DestroyCharacterItem(characterId, food.Uid, quantity)) return;
                Equipment.Remove(food.Uid, quantity);
                await WriteAsync(stream, left > 0
                    ? ConnectionProtocol.Push(Op.Ivj, ConnectionProtocol.BuildItemQuantity(food.Uid, left))
                    : ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(food.Uid)));
            }

            var fed = Pets.Fed(pet.Template, pet.Effects, experience, out int before, out int after);
            if (!DatabaseManager.UpdateCharacterItem(characterId, pet.Uid, pet.Quantity, Forgemagic.Serialize(fed))) return;
            pet.Effects.Clear();
            pet.Effects.AddRange(fed);
            bool worn = Equipment.IsWorn(pet.Position);
            if (worn) Equipment.RememberWorn(pet.Uid, pet.Position, pet.Effects);

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iuj, AlterationProtocol.BuildSlotItemChanged(pet)));
            if (worn && after != before)
                await CharacteristicsMessages.Live.SendAsync(SessionContext.Current);
            await ItemUseHandler.WeighAsync(stream);

            Console.WriteLine($"[Pets] {who}'s pet {pet.Template} eats {foods.Sum(f => f.Quantity)} food(s): " +
                              $"+{experience} experience, level {before}" + (after != before ? $" -> {after}." : "."));
        }
    }
}
