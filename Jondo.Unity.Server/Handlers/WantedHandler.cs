using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The wanted posters in play (Managers.WantedPosters): the criminal beaten and taken by whoever
    /// has his quest, and the jailer who takes him and, for 500 kamas, says whether he has been seen.
    /// </summary>
    /// <remarks>
    /// The jailer's conversation is the client's own sentences, chained the way their texts were
    /// written (tools/extract_wanted.py): «When were they last seen?» → his first answer → «I could
    /// pay you» → his second, with a «500 kamas for news of X» for each criminal the player is after
    /// → the report found → «Listen carefully» → seen, or not. «Livrer X.» is offered to whoever
    /// carries X -- caught, not yet handed over -- and ends the quest. INFERRED: the order of the
    /// chain, and that the replies are offered only to whoever has a quest they concern.
    /// </remarks>
    public static class WantedHandler
    {
        /// <summary>«No tienes suficientes kamas para poder realizar esta acción.», a warning.</summary>
        public const int NotEnoughKamas = 82;

        private static readonly Random _dice = new();

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>
        /// A won fight: every criminal in it whose quest this character has, still to be caught, is
        /// caught -- the quest's first objective -- and goes away for hours.
        /// </summary>
        public static async Task FightEndedAsync(NetworkStream stream, FightInstance fight, bool won)
        {
            if (!won) return;
            if (fight.Buscar(SessionContext.State.CharacterId) == null) return;

            var beaten = fight.Rojo.Where(m => !m.EsInvocado && m.MonsterId > 0).Select(m => m.MonsterId).Distinct();
            foreach (int monster in beaten.ToList())
            {
                var criminal = WantedPosters.ByMonster(monster);
                if (criminal == null || Quests.Log is not { } log || !Wants(log, criminal)) continue;

                await Quests.TickAsync(SessionContext.Current, criminal.Quest, criminal.Catch);
                await TakeAlongAsync(stream, criminal);
                var away = WantedPosters.Captured(monster, fight.RoleplayMapId, fight.DefenderLeaderId, Now, _dice);
                Console.WriteLine($"[Wanted] {SessionContext.State.CharacterName} takes criminal {monster} " +
                                  $"(quest {criminal.Quest})" + (away is { } h ? $"; he is away for {h.TotalHours:0.#} h." : "."));
            }
        }

        /// <summary>
        /// The caught criminal walking behind his captor, «vous devez l'obtenir en personnage suiveur»
        /// (DPLN): his item -- type 32, a follower, «148» naming the criminal's own monster -- goes in
        /// the follower's slot, and the map draws him there once the fight is over. A follower the
        /// player already has is not thrown out for him: then he goes to the bag, as he used to.
        /// </summary>
        internal static async Task TakeAlongAsync(NetworkStream stream, WantedPosters.Criminal criminal)
        {
            if (criminal.Follower == 0 || Equipment.HowMany(criminal.Follower) > 0) return;
            var slots = BonusSlots.SlotsFor(BonusSlots.FollowerType);
            bool free = slots.Any(s => !Equipment.All.Any(i => i.Position == s));
            if (free && await BonusItemHandler.PlaceAsync(stream, criminal.Follower, slots) != null) return;
            await Equipment.GiveAsync(stream, criminal.Follower, 1);
        }

        /// <summary>The criminal handed over: out of the follower's slot, and the captor drawn again without him.</summary>
        internal static async Task LetGoAsync(NetworkStream stream, WantedPosters.Criminal criminal)
        {
            if (criminal.Follower == 0) return;
            var item = Equipment.All.FirstOrDefault(i => i.Template == criminal.Follower);
            if (item == null) return;
            if (BonusSlots.IsBonusSlot(item.Position))
            {
                await AlterationHandler.RemoveSlotItemAsync(stream, SessionContext.State.CharacterId, item);
                await SlotLookHandler.RedrawAsync(stream);
            }
            else
            {
                await Equipment.TakeAsync(stream, criminal.Follower, 1);
            }
        }

        /// <summary>Whether this character is after him: the quest in hand, and he is not caught yet.</summary>
        internal static bool Wants(Jondo.Unity.World.Quests.QuestLog log, WantedPosters.Criminal criminal)
            => log.Active(criminal.Quest) && !log.ObjectiveDone(criminal.Catch);

        /// <summary>Whether this character carries him: caught, and not handed over.</summary>
        internal static bool Carries(Jondo.Unity.World.Quests.QuestLog log, WantedPosters.Criminal criminal)
            => log.Active(criminal.Quest) && log.ObjectiveDone(criminal.Catch) && !log.ObjectiveDone(criminal.Deliver);

        /// <summary>
        /// What a jailer offers on being spoken to, before his own replies: «Livrer X.» for each
        /// criminal carried that is his to take here, and «When were they last seen?» to whoever is
        /// after one of his.
        /// </summary>
        public static long[] OpeningReplies(int npc, long mapId)
        {
            var jailer = WantedPosters.JailerOf(npc);
            if (jailer == null || Quests.Log is not { } log) return Array.Empty<long>();

            var replies = new List<long>();
            bool after = false;
            foreach (var criminal in WantedPosters.HandedTo(npc))
            {
                var report = criminal.To(npc)!;
                if (Carries(log, criminal) && report.Deliver != 0 && (criminal.Map == 0 || criminal.Map == mapId))
                    replies.Add(report.Deliver);
                if (Wants(log, criminal) && report.Pay != 0) after = true;
            }
            if (after) replies.Add(jailer.Ask);
            return replies.ToArray();
        }

        /// <summary>
        /// A reply to a jailer: the next question and its replies, a question of zero to close the
        /// conversation, or null when the reply is not his bribe's nor a delivery.
        /// </summary>
        public static async Task<(long Question, long[] Replies)?> ReplyAsync(NetworkStream stream, int npc, long mapId, long reply)
        {
            var jailer = WantedPosters.JailerOf(npc);
            if (jailer == null || reply == 0 || Quests.Log is not { } log) return null;
            var close = ((long)0, Array.Empty<long>());

            // «Livrer X.»: the quest ends, its rewards with it.
            foreach (var criminal in WantedPosters.HandedTo(npc))
            {
                var report = criminal.To(npc)!;
                if (report.Deliver != reply) continue;
                if (Carries(log, criminal) && (criminal.Map == 0 || criminal.Map == mapId))
                {
                    await LetGoAsync(stream, criminal);
                    await Quests.TickAsync(SessionContext.Current, criminal.Quest, criminal.Deliver);
                    Console.WriteLine($"[Wanted] {SessionContext.State.CharacterName} hands criminal " +
                                      $"{criminal.Monster} to jailer {npc}.");
                }
                return close;
            }

            if (reply == jailer.Ask)
                return jailer.First != 0 ? (jailer.First, new long[] { jailer.Offer }) : Bribes(jailer, npc, log);
            if (reply == jailer.Offer || reply == jailer.Another) return Bribes(jailer, npc, log);
            if (reply == jailer.Keep || reply == jailer.Leave || reply == jailer.Shrug) return close;

            // «500 kamas for news of X»: paid, the report found, and «Listen carefully».
            foreach (var criminal in WantedPosters.HandedTo(npc))
            {
                var report = criminal.To(npc)!;
                if (report.Pay != reply) continue;
                if (!Wants(log, criminal)) return close;
                if (GameState.Kamas < WantedPosters.InformationPrice)
                {
                    await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                        ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, NotEnoughKamas)));
                    return close;
                }

                GameState.Kamas -= WantedPosters.InformationPrice;
                DatabaseManager.SaveCurrentCharacter();
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                    InfoMessages.KamasLost, WantedPosters.InformationPrice.ToString())));
                SessionContext.State.WantedAskedAbout = criminal.Monster;
                return (report.Found, new long[] { jailer.Listen });
            }

            // «Listen carefully»: whether the criminal asked about stands somewhere now.
            if (reply == jailer.Listen)
            {
                var criminal = WantedPosters.ByMonster(SessionContext.State.WantedAskedAbout);
                var report = criminal?.To(npc);
                if (report == null) return close;
                return WantedPosters.IsAbout(criminal!.Monster)
                    ? (report.Seen, new long[] { jailer.Another, jailer.Leave })
                    : (report.Unseen, new long[] { jailer.Another, jailer.Shrug });
            }

            return null;
        }

        /// <summary>The jailer's second answer, with a «500 kamas» for each of his criminals the player is after.</summary>
        private static (long, long[]) Bribes(WantedPosters.Jailer jailer, int npc, Jondo.Unity.World.Quests.QuestLog log)
        {
            var replies = WantedPosters.HandedTo(npc)
                .Where(c => Wants(log, c) && c.To(npc)!.Pay != 0)
                .Select(c => (long)c.To(npc)!.Pay)
                .Append(jailer.Keep)
                .ToArray();
            return (jailer.Second, replies);
        }
    }
}
