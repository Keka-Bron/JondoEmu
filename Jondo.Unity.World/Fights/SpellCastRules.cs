using Jondo.Unity.World.Maps;

namespace Jondo.Unity.World.Fights
{
    /// <summary>The stateful cast rules shared by every monster spell execution path.</summary>
    public static class SpellCastRules
    {
        public enum Rejection
        {
            None,
            Cooldown,
            PerTurn,
            PerTarget,
            NotInLine
        }

        /// <summary>
        /// Checks the rules which depend on a fighter's cast history. Range, line of sight and AP
        /// remain the caller's responsibility because they need map and spell-effect data.
        /// </summary>
        public static Rejection Check(
            Fighter caster,
            int spellId,
            long targetId,
            int sourceCell,
            int targetCell,
            int maxCastPerTurn,
            int maxCastPerTarget,
            int minCastInterval,
            bool castInLine)
        {
            if (minCastInterval > 0 &&
                caster.Recarga.TryGetValue(spellId, out int cooldown) && cooldown > 0)
            {
                return Rejection.Cooldown;
            }

            caster.LanzadosEsteTurno.TryGetValue(spellId, out int thisTurn);
            if (maxCastPerTurn > 0 && thisTurn >= maxCastPerTurn)
                return Rejection.PerTurn;

            caster.LanzadosPorObjetivo.TryGetValue((spellId, targetId), out int onTarget);
            if (targetId != 0 && maxCastPerTarget > 0 && onTarget >= maxCastPerTarget)
                return Rejection.PerTarget;

            if (castInLine && !MapGeometry.AreAligned(sourceCell, targetCell))
                return Rejection.NotInLine;

            return Rejection.None;
        }

        /// <summary>Records one accepted cast and starts its inter-turn cooldown.</summary>
        public static void Register(
            Fighter caster,
            int spellId,
            long targetId,
            int minCastInterval)
        {
            caster.LanzadosEsteTurno.TryGetValue(spellId, out int thisTurn);
            caster.LanzadosEsteTurno[spellId] = thisTurn + 1;

            if (targetId != 0)
            {
                caster.LanzadosPorObjetivo.TryGetValue((spellId, targetId), out int onTarget);
                caster.LanzadosPorObjetivo[(spellId, targetId)] = onTarget + 1;
            }

            if (minCastInterval > 0) caster.Recarga[spellId] = minCastInterval;
        }

        /// <summary>
        /// Advances cooldowns at the end of their owner's turn and clears the two per-turn
        /// counters. Cooldown keys deliberately remain present when they reach zero, matching the
        /// protocol's cooldown list.
        /// </summary>
        public static void EndTurn(Fighter caster)
        {
            AdvanceCooldowns(caster);
            ClearTurnCounters(caster);
        }

        public static void AdvanceCooldowns(Fighter caster)
        {
            foreach (int spellId in caster.Recarga.Keys.ToArray())
            {
                if (caster.Recarga[spellId] > 0) caster.Recarga[spellId]--;
            }
        }

        public static void ClearTurnCounters(Fighter caster)
        {
            caster.LanzadosEsteTurno.Clear();
            caster.LanzadosPorObjetivo.Clear();
        }
    }
}
