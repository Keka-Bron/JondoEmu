namespace Jondo.Unity.Server.Managers;

internal static class ItemBinding
{
    internal const int Character = 981;
    internal const int Account = 982;
    internal const int ExchangeableOn = 983;

    // The template's exchange marker gives a duration (or zero for an indefinite account link).
    // Store the actual exchange date on the instance so relogging never restarts the duration.
    internal static string OnPurchase(string effects, DateTimeOffset at)
    {
        var rows = Equipment.ParseEffects(effects);
        static bool HasDuration(Equipment.ItemEffect effect)
            => effect.Effect == ExchangeableOn && effect.Value is > 0 and < EffectFields.DatedSince;
        if (!rows.Any(HasDuration)) return effects;
        return Forgemagic.Serialize(rows.Select(e => HasDuration(e)
            ? new Equipment.ItemEffect(ExchangeableOn, at.AddDays(e.Value).ToUnixTimeSeconds(), 0, 0)
            : e));
    }

    internal static bool IndefiniteAccountLink(Equipment.ItemEffect effect)
        => effect.Effect == ExchangeableOn && effect.Value == 0 && effect.DiceNum == 0 && effect.DiceSide == 0;

    internal static bool CharacterOnly(IEnumerable<Equipment.ItemEffect> effects)
        => effects.Any(e => e.Effect == Character);

    internal static bool BlocksTransfer(IEnumerable<Equipment.ItemEffect> effects, DateTimeOffset now)
        => effects.Any(e => e.Effect is Character or Account
            || IndefiniteAccountLink(e)
            || (e.Effect == ExchangeableOn && e.Value >= EffectFields.DatedSince && e.Value > now.ToUnixTimeSeconds()));
}
