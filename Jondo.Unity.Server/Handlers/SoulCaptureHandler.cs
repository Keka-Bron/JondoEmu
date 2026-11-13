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

            var beaten = fight.Rojo.Where(m => !m.EsInvocado && m.MonsterId > 0).ToList();
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

            if (!await ItemUseHandler.SpendOneAsync(stream, stone.Uid)) return;
            foreach (var full in stones) await GrantAsync(stream, full);
            Console.WriteLine($"[Souls] {SessionContext.State.CharacterName} captures {souls.Count} soul(s) in {stones.Count} stone(s).");
        }

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
