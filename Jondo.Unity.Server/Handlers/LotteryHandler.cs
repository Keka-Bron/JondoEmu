using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The haven bag's lottery machine. No limit on draws: you click and something comes out.
    ///
    /// From the two real captures of using it, one with a prize and the other refused for having
    /// already used it that day:
    ///
    ///   client   iwo { f1: skill uid, f2: element }
    ///   server   iwn { f1: 1, f2: element, f4: 184, f5: who }
    ///   server   jbs { f2: 406096900 }    with a prize
    ///   server   jbs { f3: 1 }            refused
    ///
    /// f2 has the shape of an item identifier, so the prize's goes there. f3 is the refusal reason and
    /// it is never used here: the machine does not run out.
    ///
    /// After it the item goes to the bag, which is our business -- the real server delivers it by
    /// another road the capture does not get to show --, and the weight.
    ///
    /// What comes out carries effects no item of the game has -- +3 AP, +3 MP, five hundred-odd of a
    /// characteristic -- and is signed by #LOTTERY#, which is how the client draws an exomage: effect
    /// 988 is "Fabricado por: #4" (Crafted by: #4).
    /// </summary>
    public static class LotteryHandler
    {
        public static Task DrawAsync(NetworkStream stream, int elementId)
            => DrawAsync(stream, elementId, Lottery.Skill);

        public static async Task DrawAsync(NetworkStream stream, int elementId, int skillId)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    elementId, skillId, Jondo.Unity.Server.Network.SessionContext.State.CharacterId)));

            var prize = Lottery.Draw(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            if (prize == null)
            {
                Console.WriteLine("[Lotería] La tirada no ha dado nada.");
                ActivityJournal.Current.Write("lottery.empty",
                    Jondo.Unity.Server.Network.SessionContext.Current.AccountId,
                    Jondo.Unity.Server.Network.SessionContext.State.CharacterId,
                    new { elementId, skillId });
                return;
            }

            ActivityJournal.Current.Write("lottery.prize",
                Jondo.Unity.Server.Network.SessionContext.Current.AccountId,
                Jondo.Unity.Server.Network.SessionContext.State.CharacterId,
                new { elementId, skillId, uid = prize.Uid, gid = prize.Gid, quantity = prize.Quantity });

            // What the machine answers, in the capture's shape.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jbs, ConnectionProtocol.BuildLotteryResult(prize.Uid)));

            // And the prize to the BAG, which is the iua. The itd is the one that puts it in the chest,
            // and sending that one the item was created in the database but appeared nowhere.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(ChestHandler.ArrivesInBag,
                    ConnectionProtocol.BuildItemArrived(
                        ChestHandler.FieldOf(ChestHandler.ArrivesInBag), prize)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun,
                    ConnectionProtocol.BuildPods(0, 1000 + 5L * Jondo.Unity.Server.Network.SessionContext.State.TotalStrength)));
        }
    }
}
