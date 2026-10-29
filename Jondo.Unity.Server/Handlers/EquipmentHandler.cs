using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Moving an item between the bag and a slot.
    ///
    ///   C  iuk { f1: how many, f2: item uid, f3: where it goes }
    ///   S  ivq { f1: item uid, f2: where it went }
    ///   S  lym { f1: 206 }        the same 206 in every capture
    ///   S  hie { f1: 2 }          likewise
    ///   S  hii { f1: 2 }          likewise
    ///   S  iun                    pods, because what is worn still weighs
    ///
    /// Positions come from the captures and from a session of the real client: 0 the amulet,
    /// 2 to 5 the rings and the belt, 6 the hat, 7 the cloak, 8 the pet or the mount, 9 to 14 the
    /// dofus, and 63 the bag, which is where an item goes when it is taken off.
    ///
    /// The dofus slots said "12 to 14" here and that was wrong: in
    /// «Equipables/equipar 6 dofus.pcapng» the six ivq answers carry f2 = 09 0a 0b 0c 0d 0e, so
    /// they are 9 to 14 -- six slots, not three. Measured in the same session: the weapon is 1
    /// (and 0x3f, the bag, when taken off), the hat 6, the two rings 2 and 4, and the dragoturkey 8.
    ///
    /// Each slot holds one thing, and this handler enforces it: whatever was already worn goes to
    /// the bag with its own ivq before the new one goes in.
    ///
    /// Moving something also updates the cache of what is worn, which feeds
    /// StatsHandler.GetEquipBonus and with it the FIGHT sheet -- maximum life, initiative --. Not the
    /// characteristics one: that one is built with Equipment.Bonuses() over the real inventory and
    /// was never stale through this road. The cache was, before, and the fight bonuses did not
    /// arrive until the next character selection.
    /// </summary>
    public static class EquipmentHandler
    {
        /// <summary>Where an item goes when it is taken off.</summary>
        public const int Bag = 63;

        /// <summary>
        /// A legendary pet's food dropped on the worn pet: the pet rewritten (iuj, the item changed)
        /// and one of the food spent. False when the item is no such food, so it is moved as any
        /// other; true when it was, fed or not.
        /// </summary>
        private static async Task<bool> FeedLegendaryPetAsync(NetworkStream stream, long uid)
        {
            var food = Managers.Equipment.ByUid(uid);
            var what = food == null ? null : Managers.LegendaryPets.FoodOf(food.Template);
            if (food == null || what == null) return false;

            var pet = Managers.Equipment.InSlot(Managers.LegendaryPets.PetSlot);
            string? why = Managers.LegendaryPets.WhyNotFeed(pet, what);
            if (why != null)
            {
                Console.WriteLine($"[Legendary pets] Item {uid} is not fed: {why}.");
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ivq, Pb.New().Var(1, uid).Var(2, food.Position).Build()));
                return true;
            }

            var fed = Managers.LegendaryPets.Fed(pet!.Effects, what);
            if (!DatabaseManager.UpdateCharacterItem(SessionContext.State.CharacterId, pet.Uid, pet.Quantity,
                                                     Managers.Forgemagic.Serialize(fed)))
                return true;
            pet.Effects.Clear();
            pet.Effects.AddRange(fed);
            Managers.Equipment.RememberWorn(pet.Uid, pet.Position, pet.Effects);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iuj, AlterationProtocol.BuildSlotItemChanged(pet)));
            await ItemUseHandler.SpendOneAsync(stream, uid);
            Console.WriteLine($"[Legendary pets] {SessionContext.State.CharacterName}'s pet {pet.Uid} " +
                              (what.GivesStatus ? "is legendary now." : $"has the power of spell {what.Spell} now."));
            return true;
        }

        public static async Task MoveAsync(NetworkStream stream, byte[] payload, long accountId = 0)
        {
            byte[]? iuk = ConnectionProtocol.ReadPayload(payload, Op.Iuk);
            if (iuk == null || iuk.Length == 0) return;

            // Position zero, not the bag. The amulet's slot IS zero, so proto3 leaves the field
            // out and the message arrives with nothing but the uid — which is exactly what the
            // client sends when you try to put an amulet on. Defaulting to the bag answered
            // "it went back in the bag" every time, and the amulet was the one piece of equipment
            // that could never be put on.
            long uid = 0;
            int position = 0;
            foreach (var f in ProtoMessage.Parse(iuk).Fields)
            {
                if (f.WireType != 0) continue;
                if (f.FieldNumber == 2) uid = f.VarIntValue;
                else if (f.FieldNumber == 3) position = (int)f.VarIntValue;
            }
            if (uid == 0) return;

            // Food dropped on the worn pet: a legendary pet's status or its power (LegendaryPets).
            if (position == Managers.LegendaryPets.PetSlot && await FeedLegendaryPetAsync(stream, uid)) return;

            await MoveItemAsync(stream, uid, position, accountId);
        }

        /// <summary>
        /// One item to a slot or to the bag, with every frame that tells the client. Without
        /// <paramref name="sheet"/> the kub is left for the caller, which moves several and sends
        /// one at the end (a build put on, PresetHandler). False when the item could not go there.
        /// </summary>
        public static async Task<bool> MoveItemAsync(NetworkStream stream, long uid, int position, long accountId = 0,
                                                     bool sheet = true)
        {

            // WHAT CANNOT BE WORN IS NOT PUT ON. The server did not look at the item's level, so a
            // level 1 character equipped a level 110 weapon and kept its bonuses: the client draws it
            // grey and does not let it be dragged, but a tampered client sends the iuk anyway and here
            // it was accepted without asking.
            //
            // The level comes from the template's level field, which ALL 21,748 carry -- it does not
            // have to be guessed for any --. It is only checked when PUTTING ON: taking something to
            // the bag is always allowed, otherwise a character who lost levels could not undress.
            if (position != Bag)
            {
                var loSuyo = Managers.Equipment.ByUid(uid);

                // A tax collector's piece is not the character's, though its slots share their
                // numbers: it goes back where it was.
                if (loSuyo != null && Managers.Equipment.IsTaxCollectorPiece(loSuyo.Template))
                {
                    Console.WriteLine($"[Equipment] Item {uid} is a tax collector's piece. It is not put on.");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Ivq, Pb.New().Var(1, uid).Var(2, loSuyo.Position).Build()));
                    return false;
                }

                int pide = loSuyo != null ? DatabaseManager.ItemLevelRequirement(loSuyo.Template) : 0;

                if (pide > SessionContext.State.CharacterLevel)
                {
                    Console.WriteLine($"[Equipment] Item {uid} needs level {pide} and the character " +
                                      $"has {SessionContext.State.CharacterLevel}. It is not put on.");

                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                            Managers.InfoMessages.Warning, Managers.InfoMessages.LevelTooLow)));

                    // And it is put back where it was, otherwise the client leaves it drawn in the new slot
                    // until something refreshes its inventory.
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Ivq,
                            Pb.New().Var(1, uid).Var(2, loSuyo!.Position).Build()));
                    return false;
                }
            }

            // A slot holds one. Whatever was worn goes to the bag before the new one goes in, and its
            // own ivq is sent: without that both things stayed in the same slot at once and the look
            // was decided by the first one found, not by the one the player had just put on.
            var evictedUids = new System.Collections.Generic.List<long>();
            foreach (var evicted in Managers.Equipment.Occupants(position, uid))
            {
                evictedUids.Add(evicted.Uid);
                Managers.Equipment.Move(evicted.Uid, Bag);
                DatabaseManager.SaveItemPosition(evicted.Uid, Bag, SessionContext.State.CharacterId);
                Managers.Equipment.RememberWorn(evicted.Uid, Bag, evicted.Effects);

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ivq, Pb.New().Var(1, evicted.Uid).Var(2, Bag).Build()));

                Console.WriteLine($"[Equipment] Slot {position} was held by {evicted.Uid}; " +
                                  "to the bag.");
            }

            // The item may well not be ours: the inventory the client is showing is still the one
            // replayed from the capture, and those uids are not in our database. The move is
            // answered either way, which is what the real server does, and it is written down when
            // it is an item we actually hold.
            DatabaseManager.SaveItemPosition(uid, position, SessionContext.State.CharacterId);
            bool known = Managers.Equipment.Move(uid, position);

            // Without making it depend on `known`, and that matters. Managers.Equipment.LoadFrom
            // swallows its own exception, so if that read fails Items stays empty while the cache DID
            // fill when the character was chosen, which reads by another road. From then on `known`
            // would be false on every iuk and the player could take off all his clothes and keep
            // fighting with the stats on until logging out.
            Managers.Equipment.RememberWorn(uid, position, Managers.Equipment.ByUid(uid)?.Effects);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivq, Pb.New().Var(1, uid).Var(2, position).Build()));

            // The three that travel with it: the aura worn (lym) -- the 206 every equip capture
            // carries is the recorded account's --, and two of unknown meaning, each with the
            // value it carries in every equip and unequip capture there is.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lym, ConnectionProtocol.BuildAura(
                    Managers.Wardrobe.AuraOf(Jondo.Unity.Server.Network.SessionContext.State.CharacterId))));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hie, Pb.New().Var(1, 2).Build()));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hii, Pb.New().Var(1, 2).Build()));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun,
                    ConnectionProtocol.BuildPods(0, 1000 + 5L * Jondo.Unity.Server.Network.SessionContext.State.TotalStrength)));

            // And the look, which is what makes the character get on the mount without having to
            // reload the map. It is two messages and both are needed: the jsn redraws the figure on
            // the map and the lxc updates the one on the sheet. In the capture they come in this
            // order, between the three above and the weight.
            var character = DatabaseManager.GetCharacterById(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            if (character != null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                        character, Jondo.Unity.Server.Network.SessionContext.State.CellId, Jondo.Unity.Server.Network.SessionContext.State.Orientation, accountId)));

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lxc, ConnectionProtocol.BuildLookChanged(character)));
            }

            // And the sheet, because what the item gives goes with it. Without this the numbers
            // only caught up on the next entry into the world.
            if (known && sheet)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
            }

            Console.WriteLine($"[Equipment] Item {uid} -> position {position}"
                              + (position == Bag ? " (taken off)." : ".")
                              + (known ? "" : " Not one of ours; the sheet is left alone."));
            ActivityJournal.Current.Write("equipment.moved",
                accountId > 0 ? accountId : SessionContext.Current.AccountId,
                SessionContext.State.CharacterId,
                new { uid, position, known, evicted = evictedUids });
            return known;
        }
    }
}
