using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Using a zaapi.
    ///
    /// On the wire it is EXACTLY the same dance as the zaap -- iwo, iwn, hjj for the list; hjc for
    /// choosing -- and that is why the trip itself is done by <see cref="ZaapTravelHandler.TravelAsync"/>:
    /// it is the same hjc and the same destination map, so duplicating it would mean two places to fix
    /// the same bug.
    ///
    /// The only things that change are the list offered and what it costs:
    ///
    ///   the zaap    takes you to any activated zaap of the world, and charges by distance
    ///   the zaapi   takes you to the places of ITS city, and charges a flat 20 kamas
    ///
    /// The 20 come from the capture, the same in Bonta's 24 destinations and Brakmar's 21.
    /// </summary>
    public static class ZaapiTravelHandler
    {
        /// <summary>
        /// He has clicked the zaapi: he is told the element is in use and sent its list.
        ///
        /// If the map belongs to no known network an empty list is not answered: the reason is written and
        /// the element is left unopened. An empty window looks like a game bug; not opening it can at least
        /// be read in the log.
        /// </summary>
        public static async Task OpenAsync(NetworkStream stream, Interactives.Element zaapi, int skillId)
        {
            long here = SessionContext.State.MapId;

            var network = Zaapis.NetworkOn(here);
            if (network == null || network.Destinations.Count == 0)
            {
                Console.WriteLine($"[Zaapis] El mapa {here} tiene zaapi pero no hay red cargada para él.");
                return;
            }

            SessionContext.State.OpenZaapMapId = here;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    zaapi.Id, skillId, SessionContext.State.CharacterId)));

            var destinations = new List<ConnectionProtocol.ZaapDestination>();
            foreach (var destination in network.Destinations)
            {
                // The place one is already at comes out with no cost, which is what the real server does:
                // proto3 swallows the zero and the client shows it as «you are here».
                if (MapManager.GetMapInfo(destination.MapId) == null) continue;
                destinations.Add(new ConnectionProtocol.ZaapDestination(
                    destination.MapId,
                    destination.SubAreaId,
                    Interactives.LevelOfSubArea(destination.SubAreaId),
                    destination.MapId == here ? 0 : Zaapis.Cost,
                    Zaapis.Kind));
            }

            // Without f2: the zaapi's list does not carry it in any of the three captures, while the
            // zaap's always does and with THE SAME value wherever one moves -- 73400320 in eight
            // captures from different places --, so it is not «where you are» but the character's
            // saved zaap. That does not exist here yet, so in the zaap's list the map you leave from
            // is still sent; in the zaapi's, nothing, which is what the real server does.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hjj, ConnectionProtocol.BuildZaapList(
                    0, destinations, Zaapis.Teleporter)));

            Console.WriteLine($"[Zaapis] {network.City}: {destinations.Count} destinos desde el mapa {here}.");
        }
    }
}
