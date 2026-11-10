using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// A follower a quest hands over or takes back -- an item of type 32 whose «148» names the NPC --
    /// put in the follower's slot, so the map draws him walking behind the character: Manzanillo
    /// escorted to the Sadida temple («Infusión de Manzanillo», «Escolta a Manzanillo hasta el
    /// templo de Sadida»), the way a caught criminal walks behind his captor (WantedHandler).
    /// </summary>
    /// <remarks>
    /// Anything else goes through <see cref="Equipment"/> as it always did. A follower the player
    /// already walks with is not thrown out: the new one goes to the bag.
    /// </remarks>
    public static class QuestFollowers
    {
        /// <summary>Whether the item is a follower.</summary>
        public static bool IsFollower(int template) => Forgemagic.TemplateOf(template)?.Type == BonusSlots.FollowerType;

        /// <summary>The item handed over: a follower to his slot when it is free, anything else to the bag.</summary>
        public static async Task<bool> GiveAsync(NetworkStream stream, int template, int count)
        {
            if (count == 1 && IsFollower(template) && Equipment.HowMany(template) == 0)
            {
                var slots = BonusSlots.SlotsFor(BonusSlots.FollowerType);
                bool free = slots.Any(s => !Equipment.All.Any(i => i.Position == s));
                if (free && await BonusItemHandler.PlaceAsync(stream, template, slots) != null) return true;
            }
            return await Equipment.GiveAsync(stream, template, count);
        }

        /// <summary>The item taken back: a follower out of his slot and the character drawn without him.</summary>
        public static async Task<bool> TakeAsync(NetworkStream stream, int template, int count)
        {
            if (IsFollower(template))
            {
                var walking = Equipment.All.FirstOrDefault(i => i.Template == template && BonusSlots.IsBonusSlot(i.Position));
                if (walking != null)
                {
                    await AlterationHandler.RemoveSlotItemAsync(stream, SessionContext.State.CharacterId, walking);
                    await SlotLookHandler.RedrawAsync(stream);
                    return true;
                }
            }
            return await Equipment.TakeAsync(stream, template, count);
        }
    }
}
