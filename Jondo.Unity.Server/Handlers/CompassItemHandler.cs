using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The compasses of the bag: effect 1162, «Posiciona la brújula» (the client's ActionId
    /// ChangeCompassByRef), whose second number is a map reference of the client's data. Used, the
    /// compass points there: a flag on the world map at that map's coordinates, and the item stays.
    /// Meriana's compass of «Volver a ver a Dark Vlad, y seguir sin morirse» (14978, reference 186)
    /// points at Bowis' shop in [5,1] (Dofus pour les noobs: «La boussole vous indique alors de vous
    /// rendre en [5,1]»), and 25 other quest items do the same.
    /// </summary>
    /// <remarks>
    /// The flag is the client's lsd { f2: compass type, f4: { f1: x, f2: y } }: its handler (ees,
    /// on lsd) names the flag «flag_srv» and the type, and for type 0, the plain compass, labels it
    /// with the coordinates and pins it on the world map the current subarea belongs to. Type 4 is a
    /// quest's; 1 to 3 are the spouse, the party member and the hunted player, whose other fields
    /// carry who they are.
    /// </remarks>
    public static class CompassItemHandler
    {
        /// <summary>«Posiciona la brújula»: ChangeCompassByRef.</summary>
        public const int CompassByReferenceEffect = 1162;

        /// <summary>The plain compass, lrw's first value.</summary>
        public const int PlainCompass = 0;

        /// <summary>A compass used: its flag on the world map. False when the item is no compass.</summary>
        public static async Task<bool> TryUseAsync(NetworkStream stream, Equipment.Item item)
        {
            var effects = DatabaseManager.GetItemEffectsData(
                DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            var compass = effects.FirstOrDefault(e => e.EffectId == CompassByReferenceEffect);
            if (compass == null) return false;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            int reference = compass.DiceSide != 0 ? compass.DiceSide : compass.Value;
            if (!TravelData.TryMapReference(reference, out long mapId, out _)
                || MapManager.GetMapInfo(mapId) is not { } map)
            {
                Program.LogDebug($"[Items] Compass {item.Template}: map reference {reference} unknown.");
                return true;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("lsd", BuildCompass(PlainCompass, map.PosX, map.PosY)));
            Program.LogDebug($"[Items] Compass {item.Template} points at [{map.PosX},{map.PosY}] (map {mapId}).");
            return true;
        }

        /// <summary>The compass flag (lsd): its type and the world coordinates it points at.</summary>
        public static byte[] BuildCompass(int type, int x, int y)
            => Pb.New()
                .VarIfNotZero(2, type)
                .Msg(4, Pb.New().Var(1, x).Var(2, y))
                .Build();
    }
}
