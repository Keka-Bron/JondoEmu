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
    /// The items that teach an attitude: the «Pergamino de actitud» (type 173, effect 10, «Actitud
    /// #3», the attitude its first number) and the «Pócima de actitud» (type 214, effect 2823, the
    /// attitude its third).
    /// </summary>
    /// <remarks>
    /// Effect 10 is also what the Dofus and the tools wear -- an attitude while it is on --, so only
    /// the attitude scrolls take it as something to learn. The attitude learnt goes as every other
    /// does (<see cref="Emotes.LearnAsync"/>, the khi of the achievements' and quests' rewards), and
    /// the item is spent behind it. Already known, the item stays and the client's own words say so:
    /// «Verifica las actitudes que ya conoces e inténtalo de nuevo...».
    /// </remarks>
    public static class AttitudeItemHandler
    {
        public const int ScrollEffect = 10;
        public const int PotionEffect = 2823;

        /// <summary>«Pergamino de actitud», the one item type whose effect 10 is learnt and not worn.</summary>
        public const int ScrollType = 173;

        /// <summary>Effects that say something of the item and do nothing on use.</summary>
        private static readonly HashSet<int> Markers = new() { 981, 983, 2818 };

        /// <summary>The attitude an item teaches, or zero when it is no such item.</summary>
        internal static int AttitudeOf(int itemType, IReadOnlyList<DatabaseManager.ItemEffectData> effects)
        {
            var potion = effects.FirstOrDefault(e => e.EffectId == PotionEffect);
            var scroll = itemType == ScrollType ? effects.FirstOrDefault(e => e.EffectId == ScrollEffect) : null;
            var teaches = potion ?? scroll;
            if (teaches == null || !effects.All(e => ReferenceEquals(e, teaches) || Markers.Contains(e.EffectId))) return 0;
            return teaches.EffectId == PotionEffect ? teaches.Value : teaches.DiceNum;
        }

        /// <summary>An attitude item used: false when the item is no such thing.</summary>
        public static async Task<bool> TryUseAsync(NetworkStream stream, Equipment.Item item)
        {
            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            if (!effects.Any(e => e.EffectId is ScrollEffect or PotionEffect)) return false;
            int attitude = AttitudeOf(DatabaseManager.GetItemTemplateType(item.Template), effects);
            if (attitude == 0) return false;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            if (!await Emotes.LearnAsync(SessionContext.Current, attitude))
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.AttitudeAlreadyKnown)));
                return true;
            }
            await ItemUseHandler.SpendOneAsync(stream, item.Uid);
            return true;
        }
    }
}
