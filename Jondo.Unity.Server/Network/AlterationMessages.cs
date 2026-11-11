using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>State-based alteration snapshots and effects, evaluated at one explicit UTC instant.</summary>
internal sealed class AlterationMessages(Func<int, AlterationCatalogue.Definition?> definitionOf)
{
    internal static readonly AlterationMessages FromCatalogue = new(AlterationCatalogue.Of);

    /// <summary>Dofus's calendar runs 1370 years behind the Paris calendar.</summary>
    internal const int DofusYearOffset = 1370;
    private static readonly HashSet<int> MinuteEffects = [2867, 2869, 3797];
    private static readonly TimeZoneInfo Paris = FindParis();

    internal AlterationsEvent Snapshot(IEnumerable<Alterations.Active> active, DateTime nowUtc)
        => new(active.Select(alteration => Entry(alteration, nowUtc)).ToArray());

    internal Alteration Entry(Alterations.Active active, DateTime nowUtc)
    {
        var expiration = active.Kind switch
        {
            Alterations.Kind.Lasting => AlterationExpirationType.Infinite,
            Alterations.Kind.Timed => AlterationExpirationType.Date,
            Alterations.Kind.Fights => AlterationExpirationType.FightCount,
            Alterations.Kind.Victories => AlterationExpirationType.FightsWonCount,
            _ => throw new ArgumentOutOfRangeException(nameof(active), active.Kind, "Unsupported alteration expiration kind."),
        };
        long value = active.Kind switch
        {
            Alterations.Kind.Timed => active.UntilMs,
            Alterations.Kind.Fights or Alterations.Kind.Victories => active.Left,
            _ => 0,
        };
        var effects = definitionOf(active.Id)?.Effects ?? [];
        // Historical tooltip policy: three of the four measured alterations use
        // the reverse of catalogue order. Keep that established presentation order.
        return new(active.FromMs, active.Id, effects.Reverse().Select(effect => Effect(effect, active, nowUtc)).ToArray(),
            expiration, value);
    }

    internal static ObjectEffect Effect(Equipment.ItemEffect effect, Alterations.Active active, DateTime nowUtc)
    {
        int id = effect.Effect;
        ObjectEffectCase value;
        if (id is AlterationCatalogue.FightsLeft or AlterationCatalogue.VictoriesLeft)
        {
            bool counted = active.Kind is Alterations.Kind.Fights or Alterations.Kind.Victories;
            int total = counted ? active.Total : CountOf(effect);
            int left = counted ? active.Left : total;
            value = new ObjectEffectCaseValueDice(new(left, total, total));
        }
        else if (id == AlterationCatalogue.Expiry)
        {
            long until = active.Kind == Alterations.Kind.Timed ? active.UntilMs
                : checked(new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds() + CountOf(effect) * 60_000L);
            value = new ObjectEffectCaseValueDate(Date(until));
        }
        else if (MinuteEffects.Contains(id))
        {
            long minutes = effect.DiceNum;
            if (active.Kind == Alterations.Kind.Timed)
            {
                long left = checked(active.UntilMs - new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds()) / 60_000L;
                minutes = Math.Clamp(left, 0, minutes > 0 ? minutes : long.MaxValue);
            }
            value = new ObjectEffectCaseValueDice(new(checked((int)effect.Value), minutes, checked((int)effect.DiceSide)));
        }
        else
        {
            long number = effect.Value != 0 ? effect.Value : effect.DiceNum != 0 ? effect.DiceNum : effect.DiceSide;
            // Action-only effects have no selected value, not a selected integer zero.
            value = number == 0 ? new ObjectEffectCaseNone() : new ObjectEffectCaseValueInt(checked((int)number));
        }
        return new(id, value);
    }

    private static int CountOf(Equipment.ItemEffect effect)
        => checked((int)Math.Max(effect.Value, Math.Max(effect.DiceNum, effect.DiceSide)));

    internal static ObjectEffectDateValue Date(long unixMs)
    {
        var when = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMs), Paris);
        return new(when.Day, when.Year - DofusYearOffset, when.Month, when.Minute, when.Hour);
    }

    private static TimeZoneInfo FindParis()
    {
        foreach (string id in new[] { "Europe/Paris", "Romance Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }
}
