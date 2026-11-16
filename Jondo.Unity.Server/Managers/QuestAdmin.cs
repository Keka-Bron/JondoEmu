using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// A quest started, finished or forgotten by hand, in the current session: the test harness's
    /// quest route and the staff's ".quest" command both do it through here, so a quest closed by
    /// hand leaves the client in the same state whoever closed it.
    /// </summary>
    public static class QuestAdmin
    {
        /// <summary>
        /// The quest played to its end: started first if it was not, every step finished, and the
        /// client told the quest is done -- the box that follows it lets go of it only then.
        /// </summary>
        public static async Task FinishAsync(NetworkStream stream, int quest)
        {
            var log = SessionContext.State.Quests;
            if (log == null) return;
            if (!log.Active(quest) && !log.Finished(quest)) await Quests.StartAsync(stream, quest);
            bool wasActive = log.Active(quest);
            for (int guard = 0; guard < 40 && log.Active(quest); guard++) log.FinishStep(quest);
            Quests.SaveAsItStands(quest);
            if (wasActive) await DropFromTheBoxAsync(stream, quest);
            await Quests.SendJournalAsync(SessionContext.Current);
        }

        /// <summary>The quest wiped out of the character's journal, to play it again.</summary>
        public static async Task ForgetAsync(NetworkStream stream, int quest)
        {
            var log = SessionContext.State.Quests;
            if (log == null) return;
            if (log.Active(quest)) await DropFromTheBoxAsync(stream, quest);
            Quests.Forget(quest);
            await Quests.SendJournalAsync(SessionContext.Current);
        }

        /// <summary>
        /// A quest under way that is closed or wiped out of turn leaves the client's followed box as a
        /// quest ends: the client follows every quest it starts, and only the ied of a quest done takes
        /// it out again. Without it a quest wiped out while under way stayed in the box, asking for
        /// objectives the server no longer had.
        /// </summary>
        public static async Task DropFromTheBoxAsync(NetworkStream stream, int quest)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("ied", QuestProtocol.BuildQuestValidated(quest)));
            FollowedQuests.Unfollow(SessionContext.State.CharacterId, quest);
        }
    }
}
