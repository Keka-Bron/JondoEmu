using Jondo.Unity.Protocol.Game;
using Jondo.Unity.World.Combat;

namespace Jondo.Unity.Server.Network;

/// <summary>Resolved combat actions. Catalogue action IDs and semantic payload cases are independent.</summary>
internal static class CombatActions
{
    internal static GameActionFightEvent Damage(long source, int effect, long target, int loss, int element,
        int erosion = 0, int shieldLoss = 0) => LifeLost(source, effect, target, loss, Math.Max(0, element), erosion, shieldLoss);

    internal static GameActionFightEvent PushDamage(long source, long target, int loss, int erosion = 0, int shieldLoss = 0)
        => LifeLost(source, FightProtocol.PushDamage, target, loss, -1, erosion, shieldLoss);

    private static GameActionFightEvent LifeLost(long source, int effect, long target, int loss, int element, int erosion, int shieldLoss)
        => new(source, effect, new FightActionCaseLifePointsLost(new(target, loss, element, erosion, shieldLoss == 0 ? null : shieldLoss)));

    internal static GameActionFightEvent Heal(long source, int amount, long target)
        => new(source, FightProtocol.Curacion, new FightActionCaseLifePointsGain(new(amount, target)));

    internal static GameActionFightEvent Death(long source, long target)
        => new(source, FightProtocol.Died, new FightActionCaseDeath(new(target)));

    internal static GameActionFightEvent IllusionGone(long source, long target)
        => new(source, EffectSupport.IllusionGone, new FightActionCaseVanish(new(target)));

    internal static GameActionFightEvent Script(long source, int effect, int grade, int cell, int spell, int marker, bool critical)
        => new(source, effect, new FightActionCaseExecuteScript(new(critical, grade, cell, spell, marker)));

    internal static ApplySpellModifierEvent Modifier(long owner, int kind, int spell, long value, int action)
        => new(new(0, (SpellModifierActionType)action, checked((int)value), (SpellModifierType)kind, spell), owner);

    internal static RemoveSpellModifierEvent RemoveModifier(long owner, int kind, int spell, int action)
        => new((SpellModifierType)kind, (SpellModifierActionType)action, spell, owner);

    internal static FightEffectAddedEvent Buff(long target, long source, int id, int action, int effectUid,
        int value, int dice, int side, int spell, string trigger, int endRound, int dispellable, int family,
        int grade = 1, bool critical = false, int activation = -1)
    {
        var state = dispellable switch
        {
            <= 1 => EffectDissipationState.Dissipated,
            2 => EffectDissipationState.DissipatedByDeath,
            3 => EffectDissipationState.DissipatedByStrongDispel,
            4 => EffectDissipationState.ReallyNotDissipated,
            _ => throw new ArgumentOutOfRangeException(nameof(dispellable)),
        };
        var name = FightProtocol.WireTrigger(trigger);
        return new(new(new(dice, target, id, grade, new(endRound, 0),
#if JONDO_PROTOCOL_3_8_1_2
            new FightEffectTrigger(null, [], name),
#else
            name,
#endif
            effectUid, critical, value, activation >= 0 ? new(activation, 0) : new(-1, -1), side, spell,
            (FightEffectKind)family, state), source, action));
    }

    internal static GameActionFightEvent Points(long source, int effect, long target, int delta)
        => new(source, effect, new FightActionCasePointsVariation(new(delta, target)));

    internal static GameActionFightEvent PointsDodged(long source, int characteristic, long target, int amount)
        => new(source, characteristic == 1 ? FightProtocol.ActionPointsDodged : FightProtocol.MovementPointsDodged,
            new FightActionCaseDodgePointLoss(new(amount, target)));

    internal static GameActionFightEvent Displacement(long source, int effect, long target, int start, int end)
        => new(source, effect, new FightActionCaseSlide(new(start, target, end)));

    internal static GameActionFightEvent Teleport(long source, long target, int cell)
        => new(source, EffectSupport.Teleport, new FightActionCaseTeleportOnSameMap(new(cell, target, null)));

    internal static GameActionFightEvent Swap(long source, int from, long target, int to)
        => new(source, EffectSupport.SwapPositions, new FightActionCaseExchangePositions(new(from, target, to)));

    internal static GameActionFightEvent Carry(long source, int from, long target)
        => new(source, EffectSupport.Carry, new FightActionCaseCarryCharacter(new(from, target)));

    internal static GameActionFightEvent Throw(long source, long target, int cell)
        => new(source, EffectSupport.Throw, new FightActionCaseThrowCharacter(new(target, cell)));

    internal static GameActionFightEvent Visibility(long source, long target, FightInvisibilityState state)
        => new(source, EffectSupport.Visibility, new FightActionCaseInvisibility(new(state, target)));

    internal static GameActionFightEvent InvisibleDetected(long source, int cell)
        => new(source, FightProtocol.InvisibleDetected, new FightActionCaseInvisibleDetected(new(cell, source)));

    internal static GameActionFightEvent GlyphGone(long source, int id)
        => new(source, FightProtocol.RemovedGlyph, new FightActionCaseUnmarkCells(new(id)));

    internal static GameActionFightEvent GlyphTriggered(long source, int id, int cell, long target, bool walkedIn)
        => new(source, walkedIn ? FightProtocol.EnteredGlyph : FightProtocol.StartedTurnOnGlyph,
            new FightActionCaseTriggerGlyphTrap(new(cell, target, id)));

    internal static GameActionFightEvent PortalState(long source, int id, bool active)
        => new(source, FightProtocol.PortalState, new FightActionCaseActivateGlyphTrap(new(id, active)));

    internal static GameActionFightEvent TriggerStaging(long source, int sequence, int intensity = 100)
        => new(source, FightProtocol.TriggerStaging, new FightActionCasePlaySequence(new(intensity, false, sequence)));

    internal static GameActionFightEvent EffectsDuration(long source, long target, int rounds)
        => new(source, FightProtocol.EffectsDuration, new FightActionCaseModifyEffectsDuration(new(rounds, target)));

    internal static GameActionFightEvent SpellEffectsRemoved(long source, int spell, long target, int grade = 0,
        int effect = FightProtocol.SpellEffectsRemoved, bool shown = false)
        => new(source, effect, new FightActionCaseDispelSpell(new(spell, grade, target, shown)));

    internal static GameActionFightEvent Cast(long caster, long target, int cell, int spell, int level, bool critical,
        int targetCount = 0, int turnCount = 0, int weapon = 0, bool noTarget = false,
        IReadOnlyList<int>? portals = null, bool chained = false, bool silent = false, long? summoner = null)
    {
        SpellCastPerTargetCount[] hits = targetCount > 0 && target != 0 ? [new(targetCount, target)] : [];
        SpellCastCount[] counts = summoner is long owner ? [new([], 0, caster), new([], 0, owner)]
            : spell == 0 && weapon == 0 && !silent ? [] : [new(hits, turnCount, caster)];
        FightActionTargetedAbilityCase ability = spell == 0 ? new FightActionTargetedAbilityCaseWeaponGenericId(weapon)
            : new FightActionTargetedAbilityCaseSpellCast(new(portals?.ToArray() ?? [], spell, level));
        return new(caster, spell == 0 ? FightProtocol.WeaponCast : FightProtocol.Cast,
            new FightActionCaseTargetedAbility(new(silent, target != 0 || noTarget ? target : caster, counts,
                critical ? FightSpellCastCritical.CriticalHit : FightSpellCastCritical.Normal, cell,
                !chained || spell == 0, null, ability)));
    }
}
