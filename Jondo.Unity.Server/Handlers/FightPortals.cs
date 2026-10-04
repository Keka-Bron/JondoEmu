using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The portals of a fight on the wire: laid (1181), switched off (1183), gone through by a
    /// walk, a push or a Teleportal (1182), and a spell cast at one projected out of another.
    /// The rules they follow are <see cref="PortalNetwork"/>'s; here is what each one sends.
    /// </summary>
    /// <remarks>
    /// <para>What is measured, all of it in the six Selatrop captures:</para>
    /// <list type="bullet">
    /// <item>Laying one: the oldest of four out first ("jwe 310"), then "jwe 401" with f11 when
    /// it is on; a portal laid off is followed by its own "1181 off", and every other portal whose
    /// state the new one changes by its "1181" ("poner portales de selatrop", frames 109-110,
    /// 131-132, 234-235).</item>
    /// <item>Walking in: inside the walk's own sequence, after its 129 -- "307" on the portal
    /// walked into, "1181 off" for it and for the way out, "jwe 4" in the walker's name, "307"
    /// on the way out, and the walk stops there ("pegar a traves de diferentes portales",
    /// frames 185-189 and three more). Pushed in, the same, behind the push (Odisea, frames
    /// 92-97).</item>
    /// <item>A Teleportal: the same without the first 307, the one it teleports named in the
    /// jwe 4 and every 1181 in the owner's ("trascendencia y resonancia", frames 225-229).</item>
    /// <item>A turn beginning: every portal it brings back, "1181 on" in the name of whoever
    /// begins it, in a sequence of kind 2 right behind the jzc (frames 261-264).</item>
    /// <item>Aimed at a portal, a spell goes out of the network's last portal, the cast frame
    /// naming the portals and the cell it landed on, and the Selatrop's passive goes off after it
    /// (PST).</item>
    /// </list>
    /// </remarks>
    public static partial class FightHandler
    {
        // ─── Laying and switching off ─────────────────────────────────────────────────────────

        /// <summary>A portal laid by a 1181: see the remarks of the class.</summary>
        internal static async Task LayPortalAsync(FightInstance fight, Fighter owner, Outcome c)
        {
            int cell = c.PortalAt;
            // One portal a cell. INFERRED: no capture lays one on another.
            if (owner == null || fight.Portales.At(cell) != null) return;

            var displaced = fight.Portales.Displaced(owner.Id);
            if (displaced != null)
            {
                fight.Portales.Remove(displaced);
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    FightProtocol.BuildGlyphGone(owner.Id, displaced.Id)));
            }

            var portal = new Portal
            {
                Id = fight.SiguienteMarca(),
                Owner = owner.Id,
                Cell = cell,
                LayingSpell = c.HechizoOrigen,
                Grade = c.NivelOrigen,
                DiceNum = c.Efecto.DiceNum,
                DiceSide = c.Efecto.DiceSide,
                Value = c.Efecto.Value,
            };
            fight.Portales.Add(portal);

            // Whoever stood on the cell when the spell laid it counts, wherever a later row of the
            // same cast has sent him since. Its owner standing on it does not keep it off as it
            // is laid -- Estela's goes down on under its Selatrop and off right after -- somebody
            // else does (Resonancia's).
            long then = c.PortalOccupant;
            long OccupantThen(int where) => where == cell ? then : fight.OccupantOf(where);
            portal.Active = fight.Portales.ShouldBeActive(portal, OccupantThen, ignoring: owner.Id);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                FightProtocol.BuildPortal(owner.Id, portal.Id, cell, portal.DiceNum, portal.DiceSide,
                                          portal.Grade, portal.LayingSpell, portal.Active)));
            if (!portal.Active)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    FightProtocol.BuildPortalState(owner.Id, portal.Id, active: false)));
            }
            Program.LogDebug($"[Portal] {owner.Id} lays portal {portal.Id} on {cell} " +
                             $"({(portal.Active ? "on" : "off")}){(displaced != null ? $", portal {displaced.Id} goes" : "")}.");

            await RefreshPortalsAsync(fight, occupantOf: OccupantThen);
        }

        /// <summary>A 1183: the portals of its cells off until the caster's next turn begins.</summary>
        internal static async Task SwitchPortalsOffAsync(FightInstance fight, Fighter caster, IEnumerable<int> cells)
        {
            bool any = false;
            foreach (int cell in cells)
            {
                var portal = fight.Portales.At(cell);
                if (portal == null) continue;
                portal.NeutralisedBy = caster.Id;
                any = true;
            }
            if (any) await RefreshPortalsAsync(fight);
        }

        /// <summary>
        /// Every portal whose state has changed, told in its number's order, in the name of its
        /// owner -- or of <paramref name="author"/>, at a turn start.
        /// </summary>
        internal static async Task RefreshPortalsAsync(FightInstance fight, long author = 0,
                                                       System.Func<int, long> occupantOf = null)
        {
            if (fight.Portales.Count == 0) return;
            foreach (var portal in fight.Portales.Refresh(occupantOf ?? fight.OccupantOf))
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    FightProtocol.BuildPortalState(author != 0 ? author : portal.Owner, portal.Id, portal.Active)));
            }
        }

        /// <summary>
        /// A turn begins: what was gone through comes back, and what the one beginning switched
        /// off; told in a sequence of kind 2 in his name, right behind the jzc.
        /// </summary>
        internal static async Task PortalsAtTurnStartAsync(FightInstance fight, Fighter fighter)
        {
            if (fight.Portales.Count == 0) return;
            fight.Portales.TurnBegins(fighter.Id);
            var changed = fight.Portales.Refresh(fight.OccupantOf);
            if (changed.Count == 0) return;

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                FightProtocol.BuildSequenceStart(fighter.Id, FightProtocol.GlyphSequence)));
            foreach (var portal in changed)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    FightProtocol.BuildPortalState(fighter.Id, portal.Id, portal.Active)));
            }
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), fighter.Id, FightProtocol.GlyphSequence)));
        }

        // ─── Going through ────────────────────────────────────────────────────────────────────

        /// <summary>Whether this fighter may go through a portal at all: "Teleportal Imposible" keeps him out.</summary>
        internal static bool CanUsePortals(Fighter fighter)
            => fighter != null && fighter.IsAlive && !fighter.EstaCargado && !fighter.EsIlusion
               && !SpellStates.KeepsOutOfPortals(fighter);

        /// <summary>
        /// Whether a walker or a pushed fighter arriving on this cell goes through a portal: one
        /// that was on before he stepped on it.
        /// </summary>
        internal static bool PortalCatches(FightInstance fight, Fighter fighter, int cell)
        {
            var portal = fight.Portales.At(cell);
            return portal != null && portal.Active && CanUsePortals(fighter);
        }

        /// <summary>
        /// The one standing on a portal goes through the network: see the remarks of the class
        /// for what goes out. <paramref name="walkedIn"/> for a walk or a push, which send the 307
        /// of the portal entered; false for a Teleportal, which goes from under his feet and sends
        /// none. Returns whether he went.
        /// </summary>
        internal static async Task<bool> CrossPortalAsync(NetworkStream stream, FightInstance fight,
                                                         Fighter traveller, bool walkedIn)
        {
            if (!CanUsePortals(traveller)) return false;
            var entry = fight.Portales.At(traveller.CellId);
            if (entry == null) return false;
            if (walkedIn ? !entry.Active : !PortalNetwork.Usable(entry, fight.OccupantOf, ignoring: traveller.Id))
                return false;

            var chain = fight.Portales.Chain(entry, fight.OccupantOf, traveller.Id);
            if (chain.Count < 2) return false;
            var exit = chain[chain.Count - 1];

            if (walkedIn)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    FightProtocol.BuildGlyphTriggered(entry.Owner, entry.Id, entry.Cell, traveller.Id, walkedIn: false)));
            }

            entry.Used = true;
            exit.Used = true;
            foreach (var portal in new[] { entry, exit })
            {
                if (!portal.Active) continue;
                portal.Active = false;
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    FightProtocol.BuildPortalState(portal.Owner, portal.Id, active: false)));
            }

            int from = traveller.CellId;
            traveller.MoverA(exit.Cell);
            CarriedFollows(fight, traveller);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                FightProtocol.BuildTeleport(traveller.Id, traveller.Id, exit.Cell)));
            await RefreshPortalsAsync(fight);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                FightProtocol.BuildGlyphTriggered(exit.Owner, exit.Id, exit.Cell, traveller.Id, walkedIn: false)));

            Program.LogDebug($"[Portal] {traveller.Id} goes through portals {string.Join(", ", chain.Select(p => p.Id))}: " +
                             $"{from} to {exit.Cell}.");

            // Crossed (PT), and a portal of his crossed (CPT) for the owner.
            await DispararAsync(stream, fight, traveller, EffectEngine.AlCruzarUnPortal);
            var owner = fight.Buscar(entry.Owner);
            if (owner != null && owner.IsAlive)
            {
                var before = fight.TriggeringAttacker;
                fight.TriggeringAttacker = traveller;
                await DispararAsync(stream, fight, owner, EffectEngine.AlCruzarseSuPortal);
                fight.TriggeringAttacker = before;
            }
            return true;
        }

        /// <summary>
        /// Cuts a walk short on the first cell of it that holds a portal he would go through: the
        /// walk stops there, and the portal takes him on. Every walk into a portal of the captures
        /// ends on it; that one going past it stops on it too is INFERRED.
        /// </summary>
        internal static IReadOnlyList<int> StopAtThePortal(FightInstance fight, Fighter walker, IReadOnlyList<int> path)
        {
            if (fight.Portales.Count == 0 || path == null) return path;
            for (int i = 1; i < path.Count; i++)
            {
                if (PortalCatches(fight, walker, path[i])) return path.Take(i + 1).ToList();
            }
            return path;
        }

        /// <summary>
        /// The effects whose displacement takes a fighter through a portal he is moved onto: the
        /// pushes, the pulls, the steps back and forward. Measured on the step back (1041) of
        /// Odisea; the rest of the family INFERRED alike. A teleport onto a portal does not.
        /// </summary>
        internal static bool DisplacementCrossesPortals(int effect)
            => effect is World.Combat.EffectSupport.Push or World.Combat.EffectSupport.Pull
                      or World.Combat.EffectSupport.PushWithoutDamage
                      or World.Combat.EffectSupport.PushToTargetCell or World.Combat.EffectSupport.PullToTargetCell
                      or 1041 or 1042 or 1021 or 1022;

        // ─── Casting through ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Where a spell aimed at a portal comes out, and through which portals: null when it is
        /// not projected -- no portal on the cell, the portal off, a spell that deals with the
        /// portals themselves (Neutral aimed at one switches it off: "usar neutral", frame 2), or
        /// a caster the portals keep out.
        /// </summary>
        internal static (List<Portal> Chain, int Cell)? Projection(FightInstance fight, Fighter caster, int cell,
                                                                     IEnumerable<SpellEffect> effects)
        {
            if (fight.Portales.Count == 0 || caster == null) return null;
            var entry = fight.Portales.At(cell);
            if (entry == null || !entry.Active || !CanUsePortals(caster)) return null;
            if (effects != null && effects.Any(e => EffectEngine.EsDePortales(e.EffectId))) return null;

            var chain = fight.Portales.Chain(entry, fight.OccupantOf);
            if (chain.Count < 2) return null;
            int lands = PortalNetwork.Projection(caster.CellId, entry.Cell, chain[chain.Count - 1].Cell);
            if (!MapGeometry.IsValid(lands)) return null;
            return (chain, lands);
        }

        // ─── Following ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// "Sigue al lanzador" (2184): the follower walks up to his leader, as far as the effect's
        /// die, and stops in contact. His own walk in his name, inside the cast -- jto 4, jsj, jwi
        /// -- with no MP spent and the leader's facing: "jsj f1=[287, 273] f2=3 f5=-12" at frame
        /// 2125 of the Osamodas capture, his summon following him to 260.
        /// </summary>
        internal static async Task FollowAsync(FightInstance fight, Fighter follower, Fighter leader, int steps)
        {
            if (follower == null || leader == null || !follower.IsAlive || !leader.IsAlive) return;
            if (MapGeometry.Distance(follower.CellId, leader.CellId) <= 1) return;

            var walkable = MapManager.GetFightWalkable(fight.ArenaMapId);
            var occupied = new HashSet<int>();
            foreach (var other in fight.Todos)
                if (other != null && other.IsAlive && !other.EstaCargado && other != follower) occupied.Add(other.CellId);

            List<int> best = null;
            foreach (int beside in MapGeometry.GetNeighbors(leader.CellId))
            {
                if (occupied.Contains(beside)) continue;
                if (walkable != null && !walkable.Contains(beside)) continue;
                var path = MapGeometry.FindShortestPath(follower.CellId, beside, walkable, occupied);
                if (path.Count < 2) continue;
                if (best == null || path.Count < best.Count) best = path;
            }
            if (best == null) return;
            if (best.Count - 1 > steps) best = best.Take(steps + 1).ToList();

            int facing = leader.LastFacing >= 0 ? leader.LastFacing : StepOrientation(best[best.Count - 2], best[best.Count - 1]);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                FightProtocol.BuildSequenceStart(follower.Id, FightProtocol.WalkSequence)));
            await ATodosAsync(fight, ConnectionProtocol.BuildActorMoved(follower.Id,
                best.Select(c => (long)c).ToList(), facing));
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), follower.Id, FightProtocol.WalkSequence)));
            follower.MoverA(best[best.Count - 1]);
            follower.LastFacing = facing;
            CarriedFollows(fight, follower);
            Program.LogDebug($"[Fight] {follower.Id} follows {leader.Id}: {best[0]} to {best[best.Count - 1]}.");
            await RefreshPortalsAsync(fight);
        }
    }
}
