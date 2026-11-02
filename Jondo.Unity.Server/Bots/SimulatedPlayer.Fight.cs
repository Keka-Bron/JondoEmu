using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// A simulated player's fights: against monsters, in the Koliseo or in a duel, played by the
    /// client's own messages.
    /// </summary>
    /// <remarks>
    /// The exchange is the one «combate contra 4 poutchs nivel 25» shows: kaq to be ready in the
    /// placement, jwz to every jxh before a turn, jti to every jwi that closes a sequence, and on
    /// its own turn (jzc) the jrw and jwh of what it does and the jxy that passes it. What it does
    /// is decided by the server's own tactics (<see cref="MonsterTactics.PlanTurn"/>), the ones the
    /// Koliseo's JondoBots play with, over its class's spells at its level; every step and cast then
    /// goes through the socket and is checked by the fight as any player's.
    /// </remarks>
    internal sealed partial class SimulatedPlayer
    {
        /// <summary>Set while it is playing a turn, so that a second jzc does not start another.</summary>
        private int _playing;

        /// <summary>The fight it is in is the Koliseo's: it says it is back (lte) once it is over.</summary>
        private volatile bool _koliseoFight;

        /// <summary>Its pick of each pair of its class's spells, made once.</summary>
        private Dictionary<int, int>? _choices;

        private const int MostStepsPerTurn = 12;

        private async Task OnFightFrameAsync(string op, byte[] payload)
        {
            switch (op)
            {
                case Op.Jxh:
                    // "Got it": until a client says so the turn does not start.
                    await SendAsync("jwz", Pb.New().Var(1, 1).Build());
                    break;
                case Op.Jwi:
                    // Every sequence closed is acknowledged; the end of a fight waits for it.
                    await SendAsync(Op.Jti, Pb.New().Var(1, 1).Var(2, Var(payload, 1)).Build());
                    break;
                case Op.Kam:
                    // The placement: ready after a moment.
                    _ = Task.Run(ReadyAsync);
                    break;
                case Op.Jzc:
                    long whose = Var(payload, 1);
                    _ = Task.Run(() => TurnAsync(whose));
                    break;
                case Op.Kuf:
                    _ = Task.Run(AfterTheFightAsync);
                    break;
            }
        }

        private FightInstance? Fight => _characterId == 0 ? null : FightHandler.FightOf(_characterId);

        private async Task ReadyAsync()
        {
            await Task.Delay(_dice.Next(1500, 4000));
            var fight = Fight;
            if (fight == null || fight.State != FightState.Placement) return;
            if (fight.KoliseoMode >= 0) _koliseoFight = true;
            await SendAsync(Op.Kaq, Pb.New().Var(1, 1).Build());
        }

        /// <summary>A turn has started: if it is its own, or a summon's it plays, it plays it.</summary>
        private async Task TurnAsync(long whose)
        {
            var fight = Fight;
            var fighter = fight?.Buscar(whose);
            if (fight == null || fighter == null || !fighter.ControlledBy(_characterId)) return;
            if (fight.KoliseoMode >= 0) _koliseoFight = true;
            if (Interlocked.Exchange(ref _playing, 1) == 1) return;
            try { await PlayAsync(fight, whose); }
            catch (Exception ex) { Program.LogDebug($"[Bots] {_name}'s turn failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _playing, 0); }
        }

        private bool StillPlaying(FightInstance fight, long who)
            => !_closed && fight.State == FightState.Ongoing && fight.CurrentFighter?.Id == who;

        /// <summary>
        /// Its turn: the plan for the whole turn, each step checked again before it is taken -- the
        /// way the server plays a JondoBot's -- walked and cast through the socket, a placing step
        /// at the end, and the turn passed.
        /// </summary>
        private async Task PlayAsync(FightInstance fight, long who)
        {
            await Task.Delay(_dice.Next(800, 1800));
            List<MonsterTactics.Action>? plan = null;
            int next = 0;
            // What the fight turned down this turn -- a summon over the limit -- is not tried again in it.
            var refused = new HashSet<int>();

            for (int step = 0; step < MostStepsPerTurn && StillPlaying(fight, who); step++)
            {
                MonsterTactics.Action? action = null;
                await FightHandler.InTurnAsync(fight, () =>
                {
                    var me = fight.Buscar(who);
                    if (me == null || !me.IsAlive || fight.CurrentFighter?.Id != who) return Task.CompletedTask;
                    var spells = SpellsOf(me);
                    var board = FightHandler.BoardOf(fight);
                    bool Allowed(MonsterTactics.Spell s) => !refused.Contains(s.Id) && SpellCriteria.Allows(me, s.Id, s.Grade);
                    if (plan != null && next < plan.Count)
                    {
                        action = MonsterTactics.Revalidate(board, me, plan[next]);
                        if (action != null && !Allowed(action.Spell)) action = null;
                    }
                    if (action == null)
                    {
                        plan = MonsterTactics.PlanTurn(board, me, spells, Allowed);
                        next = 0;
                        action = plan.Count > 0 ? plan[0] : null;
                    }
                    next++;
                    return Task.CompletedTask;
                });
                if (action == null) break;

                var fighter = fight.Buscar(who);
                if (fighter == null) return;
                // Where it did not get to, or what it could not cast, is thought over from where it stands.
                if (action.Path.Count > 1 && !await WalkInFightAsync(fight, fighter, action.Path)) { plan = null; continue; }
                if (fighter.CellId != action.From) { plan = null; continue; }
                if (!await CastInFightAsync(fight, fighter, action.Spell.Id, action.TargetCell))
                {
                    refused.Add(action.Spell.Id);
                    plan = null;
                }
            }

            if (!StillPlaying(fight, who)) return;
            List<int>? place = null;
            await FightHandler.InTurnAsync(fight, () =>
            {
                var me = fight.Buscar(who);
                if (me != null && me.IsAlive) place = MonsterTactics.Reposition(FightHandler.BoardOf(fight), me, SpellsOf(me));
                return Task.CompletedTask;
            });
            var self = fight.Buscar(who);
            if (place != null && place.Count > 1 && self != null) await WalkInFightAsync(fight, self, place);

            if (StillPlaying(fight, who)) await SendAsync("jxy");
        }

        /// <summary>
        /// What it casts as the tactics weigh it: its class's spells at its level, with its picks; a
        /// summon of its, the summon's own.
        /// </summary>
        private List<MonsterTactics.Spell> SpellsOf(Fighter me)
        {
            if (me.Id != _characterId) return FightHandler.TacticsOf(me);
            if (_choices == null)
            {
                var pick = new Random(_number * 7919);
                _choices = SpellTable.PairsOf(me.Breed).ToDictionary(p => p.Id, p => pick.Next(2) == 0 ? p.Base : p.Variant);
            }
            var known = SpellTable.KnownFor(me.Breed, me.Level, _choices).Select(k => (k.SpellId, k.Grade));
            return FightHandler.TacticsFor(me, known);
        }

        /// <summary>The path's corners in a jrw, and the time to walk it.</summary>
        private async Task<bool> WalkInFightAsync(FightInstance fight, Fighter fighter, IReadOnlyList<int> path)
        {
            long arena = fight.ArenaMapId != 0 ? fight.ArenaMapId : fight.MapId;
            int target = path[^1];
            await SendAsync(Op.Jrw, Pb.New().Var(1, arena).Packed(2, Corners(path)).Build());
            for (int waited = 0; waited < 40 && fighter.CellId != target && StillPlaying(fight, fighter.Id); waited++)
                await Task.Delay(100);
            await Task.Delay(path.Count * 250);
            return fighter.CellId == target;
        }

        /// <summary>A cast (jwh: the cell, the spell), and the time to watch it land.</summary>
        private async Task<bool> CastInFightAsync(FightInstance fight, Fighter fighter, int spell, int cell)
        {
            int ap = fighter.CurrentAP;
            await SendAsync(Op.Jwh, Pb.New().Var(1, cell).Var(4, spell).Build());
            for (int waited = 0; waited < 30 && fighter.CurrentAP == ap && StillPlaying(fight, fighter.Id); waited++)
                await Task.Delay(100);
            await Task.Delay(_dice.Next(600, 1200));
            return fighter.CurrentAP != ap;
        }

        /// <summary>
        /// The fight is over: back in the world it says so to the Koliseo if it was there, and
        /// spends whatever levels it won.
        /// </summary>
        private async Task AfterTheFightAsync()
        {
            bool koliseo = _koliseoFight;
            _koliseoFight = false;
            for (int waited = 0; waited < 150 && Session?.State.IsInFight == true; waited++) await Task.Delay(100);
            await Task.Delay(_dice.Next(1000, 3000));
            if (koliseo) await SendAsync(Op.Lte);
            await SpendCapitalAsync();
        }
    }
}
