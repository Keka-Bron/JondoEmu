using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game = Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    public static partial class FightHandler
    {
        private static int _lastSwap;

        /// <summary>
        /// During placement, a fighter asks an ally of his side to swap cells: the ally's menu's
        /// «Cambiar de sitio», or a cell picked that the ally stands on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The client's own flow, read in its placement frame (fju, fjw) and its SwapPositionHandler:
        /// <c>FighterAllyMenuMaker.OnSwitchPlaces(cell, fighter)</c> sends <c>jzy { 1 fighter, 2 cell }</c>
        /// with the ALLY's id, where a move sends one's own. The server answers both with
        /// <c>kad { 2 requested, 3 request, 5 requester }</c>: the requested is shown the notification
        /// «ui.fight.swapPositionRequest» with «ui.common.accept» and «ui.common.refuse», which send
        /// <c>kap { 1 request }</c> and <c>kav { 1 request }</c>; the requester keeps the request. A
        /// refusal is <c>kac { 1 who refused, 2 request }</c> to both, and the requester reads
        /// «ui.fight.swapPositionRequestRefused» with his name; an accepted one is the swap itself,
        /// everybody's cells in a kmk, on which the client drops every request of a fighter that
        /// moved. <c>jzz</c>, empty, is «ui.fight.swapPositionRequestError».
        /// </para>
        /// <para>
        /// The typed offer omits the two unmapped int32s formerly populated with cells.
        /// Native use is indeterminate. An ally nobody
        /// plays -- a JondoBot, a monster of one's side -- has nobody to ask, and swaps at once
        /// (INFERRED).
        /// </para>
        /// </remarks>
        private static async Task OfferSwapAsync(FightInstance fight, Fighter requester, Fighter requested)
        {
            if (requested.IsMonster || requested.IsBot || SessionRegistry.FindByCharacter(requested.Id) == null)
            {
                await SwapCellsAsync(fight, requester, requested);
                return;
            }

            // One request between the two at a time: asking again is the same request.
            if (fight.PlacementSwaps.ContainsValue((requester.Id, requested.Id))) return;

            int request = Interlocked.Increment(ref _lastSwap);
            fight.PlacementSwaps.Add(request, (requester.Id, requested.Id));
            await ToTheTwoAsync(fight, requester.Id, requested.Id,
                new Game.FightPlacementSwapPositionsOfferEvent(requested.Id, request, requester.Id));
            Program.LogDebug($"[Fight] {requester.Id} asks {requested.Id} to swap cells (request {request}).");
        }

        /// <summary>The swap accepted (kap { 1 request }): only the one asked can.</summary>
        internal static async Task AcceptSwapAsync(GameSession session, Game.FightPlacementSwapPositionsAcceptRequest request)
        {
            bool accepted = false;
            await InPlacementTurnAsync(session, async fight =>
            {
                if (!fight.PlacementSwaps.TryGetValue(request.RequestId, out var swap) || swap.Requested != session.CharacterId
                    || !IsParticipant(fight, swap.Requester)) return;

                var requester = fight.Buscar(swap.Requester);
                var requested = fight.Buscar(swap.Requested);
                if (requester == null || requested == null || !requester.IsAlive || !requested.IsAlive
                    || requester.TeamId != requested.TeamId) return;
                // Swapping clears all offers involving either fighter, including this one.
                await SwapCellsAsync(fight, requester, requested);
                accepted = true;
            }, finishPendingFight: false);
            if (!accepted) await session.SendAsync(new Game.FightPlacementSwapPositionsErrorEvent());
        }

        /// <summary>The swap refused, or withdrawn by whoever asked (kav { 1 request }).</summary>
        internal static Task CancelSwapAsync(GameSession session, Game.FightPlacementSwapPositionsCancelRequest request)
            => InPlacementTurnAsync(session, async fight =>
            {
                long me = session.CharacterId;
                if (!fight.PlacementSwaps.TryGetValue(request.RequestId, out var swap)
                    || (swap.Requested != me && swap.Requester != me)) return;

                fight.PlacementSwaps.Remove(request.RequestId);
                await ToTheTwoAsync(fight, swap.Requester, swap.Requested, new Game.FightPlacementSwapPositionsCancelledEvent(me, request.RequestId));
            }, finishPendingFight: false);

        /// <summary>The two change places, and everybody is told where everybody stands.</summary>
        private static async Task SwapCellsAsync(FightInstance fight, Fighter one, Fighter other)
        {
            (one.CellId, other.CellId) = (other.CellId, one.CellId);
            ForgetSwapsOf(fight, one.Id);
            ForgetSwapsOf(fight, other.Id);

            await SendPlacementDispositionsAsync(fight);
            Program.LogDebug($"[Fight] {one.Id} and {other.Id} swap cells: {one.CellId} and {other.CellId}.");
        }

        /// <summary>A fighter moved: his requests go, as the client drops them on the kmk.</summary>
        private static void ForgetSwapsOf(FightInstance fight, long fighter)
        {
            foreach (var key in fight.PlacementSwaps.Where(s => s.Value.Requester == fighter || s.Value.Requested == fighter)
                                       .Select(s => s.Key).ToList())
                fight.PlacementSwaps.Remove(key);
        }

        private static Task SendPlacementDispositionsAsync(FightInstance fight)
            => ATodosAsync(fight, new Game.EntitiesDispositionEvent(fight.Todos.Select(f =>
                new Game.EntityDisposition(f.CellId, MapActorMessages.Facing(FacingOf(fight, f)), f.Id, null)).ToArray()));

        private static async Task ToTheTwoAsync(FightInstance fight, long one, long other, Game.GameEvent message)
        {
            foreach (var session in Publico(fight).Where(s => s.State.CharacterId == one || s.State.CharacterId == other))
            {
                try { await session.SendAsync(message); }
                catch (System.Exception ex) { Program.LogDebug($"[Fight] Could not write to {session.Id}: {ex.Message}"); }
            }
        }
    }
}
