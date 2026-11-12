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
    /// The cosmetics collection (CosmeticCollection), measured in «Apariencias/añadir capa objeviva
    /// y anillo objevivo a coleccion cosmeticos» and «Apariencias/retirar varias cosas de coleccion
    /// de cosmeticos…»:
    /// <code>
    ///   C lyn { 2 [uids] }       into the collection
    ///   S ium { 1 uid }          gone from the bag
    ///   S lyb { 1 [template], 3 the item, with a new uid }
    ///   S iun                    the weight
    ///   S lyg { 1 { 1 uid, 2 2 } }
    ///
    ///   C lxh { 1 [templates] }  out of the collection
    ///   S iua { 3 { 1 63, 5 the item, with a new uid } }
    ///     (lym, hie, hii, jsn, lxc when it was worn as an appearance: it is not any more)
    ///   S iun, kmb
    ///   S lya { 1 { 1 template, 2 { 1 [template] } } }
    /// </code>
    /// The collection is the account's; the entry's lwt carries it (<see cref="SendAsync"/>).
    /// </summary>
    public static class CosmeticCollectionHandler
    {
        private static Task WriteAsync(NetworkStream stream, string op, byte[] body)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(op, body));

        /// <summary>An item as the collection carries it: { 1 template, 2 effects, 3 quantity, 4 uid }.</summary>
        internal static Pb Body(CosmeticCollection.Entry entry)
        {
            var body = Pb.New().Var(1, entry.Gid);
            foreach (var effect in Equipment.ParseEffects(entry.Effects))
            {
                var line = ConnectionProtocol.EffectEntry(effect);
                if (line != null) body.Msg(2, line);
            }
            return body.Var(3, Math.Max(1, entry.Quantity)).Var(4, entry.Uid);
        }

        /// <summary>
        /// lwt { 3 heads, 4 poses, 5 bodies, repeated 6 item }: the account's collection, on entering
        /// the world, with the faces, poses and bodies it has unlocked (<see cref="AppearanceUnlocks"/>)
        /// in the lists the appearance window's tabs read (eqq::bbhb keeps them).
        /// </summary>
        public static byte[] BuildCollection(long accountId)
        {
            var lwt = Pb.New();
            foreach (var (field, kind) in new[] { (3, AppearanceUnlocks.Kind.Head), (4, AppearanceUnlocks.Kind.Idle),
                                                  (5, AppearanceUnlocks.Kind.Body) })
            {
                var ids = AppearanceUnlocks.All(accountId, kind);
                if (ids.Count > 0) lwt.Packed(field, ids.ConvertAll(id => (long)id));
            }
            foreach (var entry in CosmeticCollection.Of(accountId)) lwt.Msg(6, Body(entry));
            return lwt.Build();
        }

        public static Task SendAsync(NetworkStream stream, long accountId)
            => WriteAsync(stream, "lwt", BuildCollection(accountId));

        private static byte[] Pods()
            => ConnectionProtocol.BuildPods(0, 1000 + 5L * SessionContext.State.TotalStrength);

        /// <summary>lyn: the bag's appearance items into the collection, one of each template.</summary>
        public static async Task AddAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? lyn = ConnectionProtocol.ReadPayload(payload, "lyn");
            if (lyn == null) return;
            long account = SessionContext.Current.AccountId, character = SessionContext.State.CharacterId;

            foreach (long uid in BankHandler.PackedOrSingle(ProtoMessage.Parse(lyn).Fields, 2))
            {
                var item = Equipment.ByUid(uid);
                if (item == null || item.Position != Equipment.Bag || !CosmeticCollection.Takes(item.Template)
                    || CosmeticCollection.Has(account, item.Template))
                {
                    Console.WriteLine($"[Cosmetics] Item {uid} does not go into the collection.");
                    continue;
                }

                string effects = Forgemagic.Serialize(item.Effects);
                bool whole = item.Quantity <= 1;
                if (!DatabaseManager.DestroyCharacterItem(character, uid, 1)) continue;
                Equipment.Remove(uid, 1);
                var entry = CosmeticCollection.Add(account, item.Template, effects);
                if (entry == null) continue;

                if (whole)
                {
                    await WriteAsync(stream, Op.Ium, ConnectionProtocol.BuildItemGone(uid));
                }
                else if (HavenBagStore.FromInventory(character, uid) is { } rest)
                {
                    // The stack's new total: an iua's quantity is added to the stack the client shows.
                    await WriteAsync(stream, Op.Ivj, ConnectionProtocol.BuildItemQuantity(uid, rest.Quantity));
                }
                await WriteAsync(stream, "lyb", Pb.New().Packed(1, new long[] { entry.Gid }).Msg(3, Body(entry)).Build());
                await WriteAsync(stream, Op.Iun, Pods());
                await WriteAsync(stream, "lyg", Pb.New().Msg(1, Pb.New().Var(1, uid).Var(2, 2)).Build());
                Console.WriteLine($"[Cosmetics] {SessionContext.State.CharacterName} keeps {entry.Gid} in the collection ({entry.Uid}).");
            }
        }

        /// <summary>
        /// lxh: templates out of the collection, back into the bag. One worn as an appearance by
        /// any of the account's characters comes off them: the real window greys it out and stops
        /// drawing it, and here the slot is emptied.
        /// </summary>
        public static async Task RemoveAsync(NetworkStream stream, byte[] payload, long accountId)
        {
            byte[]? lxh = ConnectionProtocol.ReadPayload(payload, "lxh");
            if (lxh == null) return;
            long account = SessionContext.Current.AccountId, character = SessionContext.State.CharacterId;

            foreach (int gid in BankHandler.PackedOrSingle(ProtoMessage.Parse(lxh).Fields, 1).Select(g => (int)g))
            {
                // A mimisymbic's appearance stays for good: «il n'est pas possible de retirer de votre
                // collection une apparence issue d'un mimibiote».
                if (!CosmeticCollection.Takes(gid)) continue;
                var entry = CosmeticCollection.Take(account, gid);
                if (entry == null) continue;

                long uid = DatabaseManager.NextItemUid();
                if (!DatabaseManager.InsertCharacterItem(uid, character, gid, entry.Quantity, Equipment.Bag, entry.Effects))
                {
                    CosmeticCollection.Add(account, gid, entry.Effects);
                    continue;
                }
                Equipment.Add(uid, gid, entry.Quantity, Equipment.Bag, entry.Effects);
                await WriteAsync(stream, ChestHandler.ArrivesInBag, ConnectionProtocol.BuildItemArrived(
                    ChestHandler.FieldOf(ChestHandler.ArrivesInBag),
                    new HavenBagStore.StoredItem { Uid = uid, Gid = gid, Quantity = entry.Quantity, Effects = entry.Effects }));

                bool wornHere = TakeOffEverywhere(account, gid, character);
                if (wornHere) await LookChangedAsync(stream, accountId);
                await WriteAsync(stream, Op.Iun, Pods());
                if (wornHere) await BroadcastLookAsync(stream);
                await WriteAsync(stream, "lya", Pb.New().Msg(1, Pb.New().Var(1, gid)
                    .Msg(2, Pb.New().Packed(1, new long[] { gid })))
                    .Build());
                Console.WriteLine($"[Cosmetics] {SessionContext.State.CharacterName} takes {gid} out of the collection ({uid}).");
            }
        }

        /// <summary>Empties every appearance slot of the account's characters that holds the template.
        /// Whether the playing character had it on.</summary>
        private static bool TakeOffEverywhere(long account, int gid, long playing)
        {
            bool here = false;
            var characters = DatabaseManager.GetCharactersByAccountId(account).Select(c => c.Id).ToHashSet();
            characters.Add(playing);
            foreach (long id in characters)
                foreach (var worn in Wardrobe.AppearanceOf(id).Where(w => w.Gid == gid))
                {
                    Wardrobe.TakeOff(id, worn.Slot);
                    if (id == playing) here = true;
                }
            return here;
        }

        /// <summary>The playing character's new look, to him: lym, hie, hii, jsn and lxc, as the capture sends them.</summary>
        private static async Task LookChangedAsync(NetworkStream stream, long accountId)
        {
            var state = SessionContext.State;
            var character = DatabaseManager.GetCharacterById(state.CharacterId);
            if (character == null) return;
            await WriteAsync(stream, Op.Lym, ConnectionProtocol.BuildAura(Wardrobe.AuraOf(state.CharacterId)));
            await WriteAsync(stream, Op.Hie, Pb.New().Var(1, 2).Build());
            await WriteAsync(stream, Op.Hii, Pb.New().Var(1, 2).Build());
            await WriteAsync(stream, Op.Jsn, ConnectionProtocol.BuildActorRefreshed(character, state.CellId, state.Orientation, accountId));
            await WriteAsync(stream, Op.Lxc, ConnectionProtocol.BuildLookChanged(character));
        }

        /// <summary>The same look to him and to his map (kmb).</summary>
        private static async Task BroadcastLookAsync(NetworkStream stream)
        {
            var state = SessionContext.State;
            var character = DatabaseManager.GetCharacterById(state.CharacterId);
            if (character == null) return;
            byte[] kmb = ConnectionProtocol.Push(Op.Kmb,
                Pb.New().Bytes(1, BreedLookTable.MapLook(character)).Var(2, state.CharacterId).Build());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, kmb);
            await SessionRegistry.BroadcastToMapAsync(state.MapId, kmb, SessionContext.Current.Id);
        }
    }
}
