using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The mimisymbic: a real item turned into an appearance one, kept in the account's cosmetics
    /// collection. «L'équipement sera détruit. Votre Mimibiote sera consommé.»
    /// <code>
    ///   C iuu { 3 mimisymbic uid }          the double click
    ///   S hgg { 1 3, 2 mimisymbic uid }      opens the client's MimicryUi
    ///   C hmj { 1 mimisymbic uid, 2 host }   its Validate button
    ///   S ivj/ium, ium, lyb, iun            one mimisymbic spent, the host gone, kept in the collection
    ///   S hmh { 2 5 }                       success; any other value is an error
    /// </code>
    /// Not captured: read off the 3.6.10.11 client. hgg { f1, f2 } becomes the event cml, and
    /// fwy::biwi opens MimicryUi when its f1 is 3; the Validate button sends hmj with the window's
    /// m_mimicryUid and m_hostUid, in that order (eql::bbav); and eqq::bbfv reads hmh's f2: 5
    /// is the success, 1 and 4 two named errors and the rest the generic one.
    /// </summary>
    public static class MimicryHandler
    {
        private const int MimisymbicType = 166;
        private const int OpensMimicry = 3;
        private const int Success = 5;
        private const int Failure = 0;

        private static Task WriteAsync(NetworkStream stream, string op, byte[] body)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(op, body));

        /// <summary>The double click on a mimisymbic: its window opens.</summary>
        public static async Task<bool> TryOpenAsync(NetworkStream stream, Equipment.Item item)
        {
            if (DatabaseManager.GetItemTemplateType(item.Template) != MimisymbicType) return false;
            await WriteAsync(stream, "hgg", Pb.New().Var(1, OpensMimicry).Var(2, item.Uid).Build());
            return true;
        }

        /// <summary>hmj: the host goes into the collection, for one mimisymbic.</summary>
        public static async Task CreateAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? hmj = ConnectionProtocol.ReadPayload(payload, Op.Hmj);
            if (hmj == null) return;

            long mimicryUid = 0, hostUid = 0;
            foreach (var f in ProtoMessage.Parse(hmj).Fields)
            {
                if (f.WireType != 0) continue;
                if (f.FieldNumber == 1) mimicryUid = f.VarIntValue;
                else if (f.FieldNumber == 2) hostUid = f.VarIntValue;
            }

            var mimicry = Equipment.ByUid(mimicryUid);
            var host = Equipment.ByUid(hostUid);
            if (mimicry == null || host == null || host.Position != Equipment.Bag
                || DatabaseManager.GetItemTemplateType(mimicry.Template) != MimisymbicType
                || Cosmetics.MimicrySlotOf(host.Template) < 0)
            {
                Console.WriteLine($"[Mimicry] {mimicryUid} cannot take {hostUid} as host.");
                await WriteAsync(stream, "hmh", Pb.New().Var(2, Failure).Build());
                return;
            }

            // Spent before anything is kept, so a failure halfway loses nothing that was not paid for.
            if (!await ItemUseHandler.SpendOneAsync(SessionContext.Current, mimicryUid, weigh: false)
                || !await ItemUseHandler.SpendOneAsync(SessionContext.Current, hostUid, weigh: false))
            {
                await WriteAsync(stream, "hmh", Pb.New().Var(2, Failure).Build());
                return;
            }

            // Already in the collection: the duplicate is lost, as the client warns.
            var entry = CosmeticCollection.Add(SessionContext.Current.AccountId, host.Template, "");
            if (entry != null)
                await WriteAsync(stream, "lyb", Pb.New().Packed(1, new long[] { entry.Gid })
                    .Msg(3, CosmeticCollectionHandler.Body(entry)).Build());
            await ItemUseHandler.WeighAsync(stream);
            await WriteAsync(stream, "hmh", Pb.New().Var(2, Success).Build());
            Console.WriteLine($"[Mimicry] {SessionContext.State.CharacterName} turns {host.Template} into an appearance.");
        }
    }
}
