using System;
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
    /// Soul capture at the end of a won fight (Managers.SoulCapture): whoever still carries «Buscador
    /// de almas» and wears an empty soul stone has it spent, and gets the beaten group's souls in
    /// full stones, each told as an item gained.
    /// </summary>
    public static class SoulCaptureHandler
    {
        private static readonly Random _dice = new();

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        public static async Task FightEndedAsync(NetworkStream stream, FightInstance fight, bool won)
        {
            if (!won) return;
            long who = SessionContext.State.CharacterId;
            var me = fight.Buscar(who);
            if (me == null || !me.Buffs.TieneEstado(SoulCapture.SeekerState)) return;

            var stone = Equipment.InSlot(SoulCapture.Slot);
            if (stone == null) return;
            var (maxLevel, chance) = SoulCapture.PowerOf(stone.Effects);
            if (maxLevel <= 0) return;

            var beaten = fight.Rojo.Where(m => m.BeatenAsGroupMember && m.MonsterId > 0).ToList();
            if (beaten.Count == 0) return;
            bool bossRoom = DungeonHandler.IsBossRoom(fight.RoleplayMapId != 0 ? fight.RoleplayMapId : fight.MapId);
            var guardian = bossRoom
                ? beaten.Where(m => !Archimonsters.Is(m.MonsterId)).OrderByDescending(m => m.Level).FirstOrDefault()
                : null;
            var souls = beaten.Select(m => new SoulCapture.Soul(m.MonsterId, m.GradeIndex + 1, m.Level,
                                                                Archimonsters.Is(m.MonsterId), m == guardian)).ToList();

            var stones = SoulCapture.Capture(souls, maxLevel, chance, _dice);
            if (stones.Count == 0)
            {
                Console.WriteLine($"[Souls] {SessionContext.State.CharacterName}'s stone (level {maxLevel}) captures nothing: " +
                                  $"the strongest soul is level {souls.Max(s => s.Level)}.");
                return;
            }

            if (!await ItemUseHandler.SpendOneAsync(SessionContext.Current, stone.Uid)) return;
            foreach (var full in stones) await GrantAsync(stream, full);
            foreach (var monster in beaten.Select(m => m.MonsterId).Distinct())
                foreach (var (item, criterion) in DatabaseManager.GetMonsterSouls(monster))
                {
                    // Only the souls the client's data puts a condition on: the quests' (the
                    // plants of 1951, the four of 328...). The 306 without one stay unhanded until
                    // what they are for is known.
                    if (criterion.Length == 0) continue;
                    if (!FightHandler.SeLoLleva(fight, AsCarried(criterion), monsterId: monster)) continue;
                    if (await CommandHandler.GrantItemAsync(stream, item, 1) != null)
                        Console.WriteLine($"[Souls] {SessionContext.State.CharacterName}'s capture of monster {monster} yields item {item}.");
                }
            Console.WriteLine($"[Souls] {SessionContext.State.CharacterName} captures {souls.Count} soul(s) in {stones.Count} stone(s).");
        }

        /// <summary>
        /// A soul's criterion with «Qo!X» on an objective that wants an item brought (type 3) read as
        /// that item not carried yet. The eight soul rows that use it all pair it with «Qo&lt;X» on the
        /// same objective -- «Qa=1951&amp;Qo&gt;13679&amp;Qo!13680&amp;Qo&lt;13680», 13680 being «bring
        /// 34209 to Jauel», in the same step as 13679 --, so read as «not in hand» it could never hold
        /// while the objective is shown, and the quest asks for the soul right then. Read this way it
        /// is the «PO!» guard the other soul rows carry («Qs=903&amp;Qo&lt;2677&amp;PO!34236»): one soul.
        /// </summary>
        internal static string AsCarried(string criterion)
            => System.Text.RegularExpressions.Regex.Replace(criterion, @"Qo!(\d+)", m =>
            {
                var objective = Quests.Book?.Objective(int.Parse(m.Groups[1].Value));
                return objective is { TypeId: 3 } && objective.Parameters.Count >= 2
                       && (objective.Parameters.Count < 3 || objective.Parameters[2] <= 1)
                    ? "PO!" + objective.Parameters[1]
                    : m.Value;
            });

        /// <summary>A full stone into the bag, with its souls, and the «you have gained» line.</summary>
        public static async Task<bool> GrantAsync(NetworkStream stream, SoulCapture.Stone full)
        {
            var item = Equipment.Create(full.Item, 1, full.Effects);
            if (item == null) return false;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iua, ConnectionProtocol.BuildItemArrived(3,
                new HavenBagStore.StoredItem
                {
                    Uid = item.Uid,
                    Gid = full.Item,
                    Quantity = 1,
                    Effects = Forgemagic.Serialize(full.Effects),
                })));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained, "1", full.Item.ToString())));
            return true;
        }
    }
}
