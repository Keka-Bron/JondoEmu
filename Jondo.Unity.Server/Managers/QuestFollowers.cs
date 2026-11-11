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
    /// templo de Sadida»), the way a caught criminal walks behind his captor (WantedHandler). And
    /// the roleplay disguises (type 31, «149» a look) the same way, in theirs: Uba Teseo's wabbit
    /// transformation, lost in the first fight («attention de ne pas lancer de combats afin de ne
    /// pas perdre l'apparence», Dofus pour les noobs). And the curses a conversation casts (type
    /// 30) to theirs: the Kama Baraka's «Fiebre del juego» for whoever wins («vous obtiendrez un lot
    /// et la malédiction la Fièvre du Jeu», Dofus pour les noobs).
    /// </summary>
    /// <remarks>
    /// Anything else goes through <see cref="Equipment"/> as it always did. A follower the player
    /// already walks with is not thrown out: the new one goes to the bag.
    /// </remarks>
    public static class QuestFollowers
    {
        /// <summary>Whether the item is a follower, a roleplay disguise or a curse.</summary>
        public static bool IsFollower(int template)
            => Forgemagic.TemplateOf(template)?.Type is BonusSlots.FollowerType or BonusSlots.RoleplayBuffType
                                                      or BonusSlots.CurseType;

        /// <summary>The item handed over: a follower to his slot when it is free, anything else to the bag.</summary>
        public static async Task<bool> GiveAsync(NetworkStream stream, int template, int count)
        {
            if (count == 1 && IsFollower(template) && Equipment.HowMany(template) == 0)
            {
                var slots = BonusSlots.SlotsFor(Forgemagic.TemplateOf(template)!.Type);
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
