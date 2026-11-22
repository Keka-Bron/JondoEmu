using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Game = Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using static Jondo.Protocol.NetworkMessage;

namespace Jondo.Unity.Server.Handlers
{
    public static partial class FightHandler
    {
        /// <summary>The swaps asked for and not yet answered: by fight and request, who asked whom.</summary>
        private static readonly ConcurrentDictionary<(long Fight, int Request), (long Requester, long Requested)> _swaps = new();
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
            ForgetStaleSwaps();
            if (requested.IsMonster || requested.IsBot || SessionRegistry.FindByCharacter(requested.Id) == null)
            {
                await SwapCellsAsync(fight, requester, requested);
                return;
            }

            // One request between the two at a time: asking again is the same request.
            if (_swaps.Any(s => s.Key.Fight == fight.FightId && s.Value == (requester.Id, requested.Id))) return;

            int request = Interlocked.Increment(ref _lastSwap);
            _swaps[(fight.FightId, request)] = (requester.Id, requested.Id);
            await ToTheTwoAsync(fight, requester.Id, requested.Id,
                new Game.FightPlacementSwapPositionsOfferEvent(requested.Id, request, requester.Id));
            Program.LogDebug($"[Fight] {requester.Id} asks {requested.Id} to swap cells (request {request}).");
        }

        /// <summary>The swap accepted (kap { 1 request }): only the one asked can.</summary>
        public static async Task AcceptSwapAsync(NetworkStream stream, byte[] payload)
        {
            long me = SessionContext.State.CharacterId;
            var fight = FightOf(me);
            int request = Network.FightProtocol.ReadSwapRequest(payload, Op.Kap);
            if (fight == null)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jzz, System.Array.Empty<byte>()));
                return;
            }

            await InTurnAsync(fight, async () =>
            {
                if (!_swaps.TryRemove((fight.FightId, request), out var swap) || swap.Requested != me)
                {
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jzz, System.Array.Empty<byte>()));
                    return;
                }

                var requester = fight.Buscar(swap.Requester);
                var requested = fight.Buscar(swap.Requested);
                if (fight.State != FightState.Placement || requester == null || requested == null
                    || !requester.IsAlive || !requested.IsAlive || requester.TeamId != requested.TeamId)
                {
                    await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jzz, System.Array.Empty<byte>()));
                    return;
                }
                await SwapCellsAsync(fight, requester, requested);
            });
        }

        /// <summary>The swap refused, or withdrawn by whoever asked (kav { 1 request }).</summary>
        public static async Task RefuseSwapAsync(NetworkStream stream, byte[] payload)
        {
            long me = SessionContext.State.CharacterId;
            var fight = FightOf(me);
            int request = Network.FightProtocol.ReadSwapRequest(payload, Op.Kav);
            if (fight == null || !_swaps.TryGetValue((fight.FightId, request), out var swap)
                || (swap.Requested != me && swap.Requester != me)) return;

            if (!_swaps.TryRemove((fight.FightId, request), out _)) return;
            await ToTheTwoAsync(fight, swap.Requester, swap.Requested, new Game.FightPlacementSwapPositionsCancelledEvent(me, request));
            Program.LogDebug($"[Fight] {me} turns down the swap {request}.");
        }

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
            foreach (var key in _swaps.Where(s => s.Key.Fight == fight.FightId
                                                  && (s.Value.Requester == fighter || s.Value.Requested == fighter))
                                      .Select(s => s.Key).ToList())
                _swaps.TryRemove(key, out _);
        }

        /// <summary>Requests of fights that are no longer placing, or no longer are.</summary>
        private static void ForgetStaleSwaps()
        {
            foreach (var key in _swaps.Keys.ToList())
            {
                var fight = _activeFights.TryGetValue(key.Fight, out var f) ? f : null;
                if (fight == null || fight.State != FightState.Placement) _swaps.TryRemove(key, out _);
            }
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
