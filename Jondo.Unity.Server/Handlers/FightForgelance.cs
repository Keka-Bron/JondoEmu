using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    public static partial class FightHandler
    {
        private static async Task AnnounceForgelanceChildAsync(FightInstance fight, Outcome outcome)
        {
            var caster = outcome.Caster;
            var limits = LimitesDeGrado(outcome.HechizoOrigen, outcome.NivelOrigen);
            if (limits.LevelId <= 0) return;
            int cell = outcome.CeldaDelLanzamiento >= 0 ? outcome.CeldaDelLanzamiento
                : outcome.Sobre?.CellId ?? caster.CellId;
            var target = outcome.Sobre is { IsAlive: true } ? outcome.Sobre : null;
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                FightProtocol.BuildAction(caster.Id, FightProtocol.Cast,
                    FightProtocol.CastAt(caster.Id, target?.Id ?? 0, cell,
                        outcome.HechizoOrigen, limits.LevelId, critical: false,
                        noTarget: target == null, chained: true), FightProtocol.CastDetail)));
        }

        /// <summary>
        /// The client chooses the blue/green lance icon from states 3361/3589. They are ordinary
        /// visible state buffs (family 2), as in LancePierre packets 20/24. Refresh on walking,
        /// displacement and summon creation; only the owner's own lance grants the green state.
        /// </summary>
        internal static async Task SyncForgelanceProximityAsync(NetworkStream stream, FightInstance fight)
        {
            foreach (var owner in fight.Azul.Concat(fight.Rojo).Where(f => f.IsAlive && ForgelanceCombat.Is(f)).ToList())
            {
                var lance = ForgelanceCombat.OwnLance(fight, owner);
                bool near = owner.Buffs.TieneEstado(ForgelanceCombat.Disarmed)
                            && ForgelanceCombat.IsNear(owner, lance);
                bool had = owner.Buffs.TieneEstado(ForgelanceCombat.NearLance);
                if (near == had) continue;
                if (near)
                {
                    var state = SpellEffects.De(24391, 2).Where(e => e.EffectId == 950
                        && e.Value == ForgelanceCombat.NearLance).ToList();
                    await AplicarEfectosAsync(stream, fight, lance, 24391, 2, owner,
                        EffectEngine.AlLanzar, owner.CellId, tirada: state, soloAlObjetivo: true);
                }
                else
                {
                    foreach (var row in owner.Buffs.QuitarEstadoConEmbrujos(ForgelanceCombat.NearLance))
                        await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya,
                            FightProtocol.BuildBuffGone(owner.Id, row.Numero)));
                    await DispararAsync(stream, fight, owner,
                        EffectEngine.AlQuitarseElEstado(ForgelanceCombat.NearLance));
                }
            }
        }
    }
}
