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
        private static async Task AnnounceForgelanceChildAsync(FightInstance fight, Fighter caster,
                                                               int spell, int grade, int cell, Fighter target)
        {
            var limits = LimitesDeGrado(spell, grade);
            if (limits.LevelId <= 0) return;
            // Eclipse's automatic impact uses the full cast animation at the end of its chain.
            bool chained = spell != ForgelanceCombat.SigelDisc || grade is not (3 or 6);
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe,
                FightProtocol.BuildAction(caster.Id, FightProtocol.Cast,
                    FightProtocol.CastAt(caster.Id, target?.Id ?? 0, cell, spell, limits.LevelId,
                        critical: false, noTarget: target == null, chained: chained), FightProtocol.CastDetail)));
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
                if (lance != null) await SyncLanceProtectionAsync(stream, fight, lance);
                bool near = owner.Buffs.TieneEstado(ForgelanceCombat.Disarmed)
                            && ForgelanceCombat.IsNear(owner, lance);
                bool had = owner.Buffs.TieneEstado(ForgelanceCombat.NearLance);
                if (near == had) continue;
                if (near)
                {
                    var state = SpellEffects.De(ForgelanceCombat.LanceAura, 2).Where(e => e.EffectId == 950
                        && e.Value == ForgelanceCombat.NearLance).ToList();
                    await AplicarEfectosAsync(stream, fight, lance, ForgelanceCombat.LanceAura, 2, owner,
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

        private static async Task SyncLanceProtectionAsync(NetworkStream stream, FightInstance fight, Fighter lance)
        {
            var aura = fight.Glifos.FirstOrDefault(g => g.Dueno == lance.Id && g.Grado == 2
                && ForgelanceCombat.IsAura(fight, g));
            var protection = SpellEffects.De(ForgelanceCombat.LanceAura, 2)
                .Where(e => e.EffectId == Buffs.DanoSufridoPorCiento).ToList();

            foreach (var ally in fight.Todos.Where(f => f.IsAlive && f != lance && f.TeamId == lance.TeamId).ToList())
            {
                var row = ally.Buffs.Puestos.FirstOrDefault(b => b.Quien == lance.Id
                    && b.HechizoOrigen == ForgelanceCombat.LanceAura && b.EffectId == Buffs.DanoSufridoPorCiento);
                bool inside = aura?.Cubre(ally.CellId) == true;
                if (inside && row == null)
                {
                    var effects = EffectEngine.Resolver(fight, lance, aura.Hechizo, aura.Grado, ally,
                        EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: ally.CellId,
                        efectosSorteados: protection, soloAlObjetivo: true);
                    // The official buff lasts while the fighter stays in the aura.
                    foreach (var effect in effects) effect.Buff.CaducaEnRonda = -1;
                    await AplicarEfectosAsync(stream, fight, lance, aura.Hechizo, aura.Grado, ally,
                        EffectEngine.AlLanzar, ally.CellId, preparedEffects: effects);
                }
                else if (!inside && row != null && ally.Buffs.QuitarFila(row))
                {
                    await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jya,
                        FightProtocol.BuildBuffGone(ally.Id, row.Numero)));
                }
            }
        }

        private static async Task RemoveLanceMarksAsync(NetworkStream stream, FightInstance fight, Fighter lance)
        {
            var marks = fight.Glifos.Where(g => g.Dueno == lance.Id && ForgelanceCombat.IsAura(fight, g)).ToList();
            foreach (var mark in marks) fight.Glifos.Remove(mark);
            foreach (var mark in marks)
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jwe, FightProtocol.BuildGlyphGone(mark.Dueno, mark.Id)));

            await SyncLanceProtectionAsync(stream, fight, lance);
        }
    }
}
