using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Walking in a fight, and what the enemies around a cell take from whoever leaves it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One walk for everybody -- a player's jrw and a monster's planned path go through
    /// <see cref="WalkPathAsync"/> -- because the rule is one: every cell left while standing next
    /// to an enemy able to tackle costs what <see cref="Tackle"/> says, and it is the same whether
    /// the one leaving is a person or a monster. The seven tackles of the captures hold both: a
    /// player held by a monster, a monster held by a player's summon, a summon held by a player.
    /// </para>
    /// <para>
    /// What is measured about the walk itself:
    /// </para>
    /// <list type="bullet">
    /// <item>A path is paid cell by cell. "atacar a recaudador y perder", frames 361-377: the
    /// collector walks five cells into contact with a summon, then the jwe 104 and its AP loss,
    /// then the one cell left, all inside ONE walk sequence with a jsj and a 129 per stretch.</item>
    /// <item>Each stretch is "jsj, the MP sheet inside its own jto 3 / jwi 3, jwe 129", in that
    /// order: 1,729 of the 1,732 jsj of the captures' walks.</item>
    /// <item>Every jsj of a walk carries the walk's final facing: what the client asked for in its
    /// jrw when a person walks, else the way the last step goes -- 1 for +x, 3 for -y, 5 for -x
    /// and 7 for +y. 1,687 of the 1,713 unsplit walks face their last step, and 25 of the other 26
    /// carry an even, diagonal facing; the 19 stretches of split walks all
    /// carry the final facing, even where their own last step turns elsewhere.</item>
    /// <item>Who does not hold, or is not held: a template without the CanTackle bit (26 walks
    /// away from training dummies, none tackled), a state flagged cantTackle or cantBeTackled
    /// in the client's SpellStateData (No Placable 96 on the walker twice in the challenge
    /// capture, Gladiatroolear 5970 at the troll fair), and an invisible fighter on either side
    /// (four players made invisible walk away from dream monsters unheld, frames 2833-4141 of
    /// "entrar a sueño mediante invitacion"; a player walks away from an invisible enemy unheld
    /// in "koliseo completo", frame 2607). The only invisibility this server has is the
    /// Tymador's, hidden among his copies.</item>
    /// </list>
    /// <para>
    /// INFERRED: a path longer than what a tackle leaves him stops where his MP run out -- no
    /// capture has a walker asking for more than he keeps. A tackle that takes nothing is not
    /// announced and does not split the path; the captures only show the 104 when something is
    /// taken.
    /// </para>
    /// </remarks>
    public static partial class FightHandler
    {
        // ─── Who holds whom ──────────────────────────────────────────────────────────────────

        /// <summary>Whether this fighter holds whoever tries to leave the four cells around him.</summary>
        internal static bool CanTackle(Fighter fighter)
        {
            if (fighter == null || !fighter.IsAlive || !fighter.TemplateAllowsTackle) return false;
            // A carried fighter shares his carrier's cell and holds none of his own.
            if (fighter.EstaCargado || fighter.HiddenAmongCopies) return false;
            foreach (int stateId in fighter.Buffs.Estados)
            {
                if (SpellStates.Of(stateId)?.CantTackle == true) return false;
            }
            return true;
        }

        /// <summary>Whether leaving a cell next to his enemies costs this fighter anything.</summary>
        internal static bool CanBeTackled(Fighter fighter)
        {
            if (fighter == null || fighter.HiddenAmongCopies) return false;
            foreach (int stateId in fighter.Buffs.Estados)
            {
                if (SpellStates.Of(stateId)?.CantBeTackled == true) return false;
            }
            return true;
        }

        /// <summary>Escape (78) as it stands this round: the sheet plus whatever buffs say.</summary>
        internal static int EscapeOf(Fighter fighter, int round)
            => fighter.Otra(Tackle.EscapeCharacteristic) + fighter.Buffs.De(Tackle.EscapeCharacteristic, round);

        /// <summary>Tackle (79) as it stands this round: the sheet plus whatever buffs say.</summary>
        internal static int TackleOf(Fighter fighter, int round)
            => fighter.Otra(Tackle.TackleCharacteristic) + fighter.Buffs.De(Tackle.TackleCharacteristic, round);

        /// <summary>
        /// A tenth of the agility, and the template's own bonus on top: the escape and tackle a
        /// monster fights with. See <see cref="BuildMonsterFighter"/>.
        /// </summary>
        internal static void SetTackleSheet(Fighter fighter, int agility, int escapeBonus, int tackleBonus)
        {
            const int AgilityPerPoint = 10;
            fighter.Otras[Tackle.EscapeCharacteristic] = agility / AgilityPerPoint + escapeBonus;
            fighter.Otras[Tackle.TackleCharacteristic] = agility / AgilityPerPoint + tackleBonus;
        }

        /// <summary>
        /// Who holds <paramref name="mover"/> on <paramref name="cell"/>, and what leaving it
        /// takes out of the points given. Nobody and nothing when he cannot be held.
        /// </summary>
        internal static (List<Fighter> Tacklers, Tackle.Loss Loss) TackleAt(FightInstance fight, Fighter mover,
                                                                            int cell, int actionPoints, int movementPoints)
        {
            if (fight == null || mover == null || !CanBeTackled(mover))
                return (new List<Fighter>(), Tackle.Loss.None);

            var tacklers = Tackle.TacklersAround(cell, fight.Enemigos(mover.Id).Where(e => e != mover), CanTackle);
            if (tacklers.Count == 0) return (tacklers, Tackle.Loss.None);

            int round = fight.RoundNumber;
            var loss = Tackle.Resolve(EscapeOf(mover, round),
                                      tacklers.Select(t => TackleOf(t, round)).ToList(),
                                      actionPoints, movementPoints);
            return (tacklers, loss);
        }

        // ─── The walk ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Walks <paramref name="path"/> -- its first cell is where the mover stands -- and says
        /// so to the whole fight, paying every tackle on the way. Returns the cells actually
        /// walked, the first one included: fewer than asked when a tackle leaves him without
        /// the MP for the rest.
        /// </summary>
        /// <param name="facing">
        /// The facing every jsj of the walk carries: the client's own when a person walks, and
        /// by default the way the path's last step goes.
        /// </param>
        /// <param name="stream">
        /// The acting session's stream, for what going through a portal sets off (PT, CPT).
        /// </param>
        internal static async Task<List<int>> WalkPathAsync(FightInstance fight, Fighter mover, IReadOnlyList<int> path,
                                                            int facing = -1, System.Net.Sockets.NetworkStream stream = null)
        {
            var walked = new List<int> { path[0] };
            if (path.Count < 2) return walked;
            // A walk ends on the first portal of it that takes him on (FightPortals.cs).
            path = StopAtThePortal(fight, mover, path);
            if (facing < 0) facing = StepOrientation(path[path.Count - 2], path[path.Count - 1]);
            mover.LastFacing = facing;

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(mover.Id, Network.FightProtocol.WalkSequence)));

            var stretch = new List<int> { path[0] };
            for (int i = 0; i < path.Count - 1; i++)
            {
                int here = path[i];
                int movementLeft = mover.CurrentMP - (stretch.Count - 1);
                var (tacklers, loss) = TackleAt(fight, mover, here, mover.CurrentAP, movementLeft);
                if (loss.Any)
                {
                    // The stretch walked so far goes out first: the 104 is paid on the cell the
                    // mover has reached, not on the one he set off from.
                    if (stretch.Count > 1)
                    {
                        await AnnounceStretchAsync(fight, mover, stretch, facing);
                        stretch = new List<int> { here };
                    }
                    await PayTackleAsync(fight, mover, tacklers, loss);
                    movementLeft = mover.CurrentMP;
                }

                if (movementLeft <= 0) break;
                stretch.Add(path[i + 1]);
                walked.Add(path[i + 1]);
            }
            if (stretch.Count > 1) await AnnounceStretchAsync(fight, mover, stretch, facing);

            // Once for the whole walk, so that effect 1100 -- back to the previous position --
            // undoes the walk and not only its last stretch. And a portal he has walked onto
            // takes him on inside the walk's own sequence, after its 129: "pegar a traves de
            // diferentes portales", frames 185-190.
            bool onAPortal = walked.Count > 1 && PortalCatches(fight, mover, walked[walked.Count - 1]);
            mover.MoverA(walked[walked.Count - 1]);
            CarriedFollows(fight, mover);
            if (onAPortal) await CrossPortalAsync(stream, fight, mover, walkedIn: true);
            else if (walked.Count > 1) await RefreshPortalsAsync(fight);

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), mover.Id,
                                                       Network.FightProtocol.WalkSequence)));
            return walked;
        }

        /// <summary>One stretch of a walk: the path, the MP left in a sheet sequence, and the 129.</summary>
        private static async Task AnnounceStretchAsync(FightInstance fight, Fighter mover, List<int> stretch, int facing)
        {
            int steps = stretch.Count - 1;
            mover.CurrentMP -= steps;

            await ATodosAsync(fight, ConnectionProtocol.BuildActorMoved(mover.Id,
                stretch.Select(c => (long)c).ToList(), facing));
            await AnnouncePointsAsync(fight, mover, MovementPointsCharacteristic, mover.CurrentMP);
            // The 129 in the shape of any row of points, f3 / f14 / f20 in that order: byte for
            // byte the tutorial's frame 491, which BuildAction's f20-before-f14 is not.
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                Network.FightProtocol.BuildPointsLost(mover.Id, Network.FightProtocol.Walked, mover.Id, steps)));

            var statistics = StatisticsBehind(fight, mover);
            if (statistics != null) statistics.MovementPointsSpent += steps;
        }

        /// <summary>
        /// The tackle paid: the 104 naming who holds him, then each kind of point he loses as
        /// its sheet and its row -- the AP first, the MP after, and neither when it is none.
        /// </summary>
        private static async Task PayTackleAsync(FightInstance fight, Fighter mover, List<Fighter> tacklers,
                                                 Tackle.Loss loss)
        {
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                TackleProtocol.BuildTackled(mover.Id, tacklers.Select(t => t.Id))));

            if (loss.ActionPoints > 0)
            {
                mover.CurrentAP -= loss.ActionPoints;
                await AnnouncePointsAsync(fight, mover, ActionPointsCharacteristic, mover.CurrentAP);
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildPointsLost(mover.Id, Network.FightProtocol.ActionPointsLost,
                                                          mover.Id, loss.ActionPoints)));
            }
            if (loss.MovementPoints > 0)
            {
                mover.CurrentMP -= loss.MovementPoints;
                if (fight.CurrentFighter == mover) fight.TurnTackledMp += loss.MovementPoints;
                await AnnouncePointsAsync(fight, mover, MovementPointsCharacteristic, mover.CurrentMP);
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                    Network.FightProtocol.BuildPointsLost(mover.Id, Network.FightProtocol.MovementPointsLost,
                                                          mover.Id, loss.MovementPoints)));
            }

            Program.LogDebug($"[Fight] {mover.Id} is tackled by {string.Join(", ", tacklers.Select(t => t.Id))}: " +
                             $"-{loss.ActionPoints} AP, -{loss.MovementPoints} MP.");
        }

        /// <summary>One kind of points, in the short sheet sequence the real server wraps every jxw in.</summary>
        private static async Task AnnouncePointsAsync(FightInstance fight, Fighter mover, int characteristic, int value)
        {
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jto,
                Network.FightProtocol.BuildSequenceStart(mover.Id, Network.FightProtocol.SheetSequence)));
            await FichaATodosAsync(fight, mover.Id, (characteristic, (long)value, 0L, 0L));
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwi,
                Network.FightProtocol.BuildSequenceEnd(fight.SiguienteAccion(), mover.Id,
                                                       Network.FightProtocol.SheetSequence)));
        }

        /// <summary>
        /// The facing of a step: one of +x is 1, of -y is 3, of -x is 5, and of +y is 7. What a
        /// monster's walk faces, from its last step; see the remarks above for the census.
        /// </summary>
        internal static int StepOrientation(int from, int to)
        {
            var (fx, fy) = MapGeometry.CellToPoint(from);
            var (tx, ty) = MapGeometry.CellToPoint(to);
            int dx = tx - fx, dy = ty - fy;
            if (dx > 0 && dy == 0) return 1;
            if (dx == 0 && dy < 0) return 3;
            if (dx < 0 && dy == 0) return 5;
            if (dx == 0 && dy > 0) return 7;
            return 1;
        }
    }
}
