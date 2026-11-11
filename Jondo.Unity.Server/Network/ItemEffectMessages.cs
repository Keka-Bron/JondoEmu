using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>Item-owned effect values, shared by semantic snapshots and legacy item serializers.</summary>
internal sealed class ItemEffectMessages(Func<int, long, long, long, (int Field, long V1, long V2, long V3)> shape)
{
    internal static ItemEffectMessages FromCatalogue { get; } = new(EffectFields.Shape);

    internal ObjectEffect? Effect(Equipment.ItemEffect effect)
    {
        // Internal smithmagic metadata is never a client effect.
        if (effect.Effect <= 0) return null;
        if (!string.IsNullOrEmpty(effect.Text))
            return new(effect.Effect, new ObjectEffectCaseValueString(effect.Text));

        var (field, first, second, third) = shape(effect.Effect, effect.Value, effect.DiceNum, effect.DiceSide);
        if (field == EffectFields.Skip) return null;
        ObjectEffectCase value = field switch
        {
            EffectFields.NoValue => new ObjectEffectCaseNone(),
            // Selected zero remains distinct from an action-only effect.
            EffectFields.AsNumber => new ObjectEffectCaseValueInt(checked((int)first)),
            EffectFields.AsRange => new ObjectEffectCaseValueMinMax(new(checked((int)first), checked((int)second))),
            EffectFields.AsDice => new ObjectEffectCaseValueDice(new(checked((int)first), second, checked((int)third))),
            EffectFields.AsDate => new ObjectEffectCaseValueDate(Date(first)),
            _ => throw new InvalidOperationException($"Unsupported item effect shape {field}."),
        };
        return new(effect.Effect, value);
    }

    internal static ObjectEffectDateValue Date(long unixSeconds)
    {
        var when = OsavoraSeasons.InParis(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        return new(when.Day, when.Year - 1370, when.Month, when.Minute, when.Hour);
    }

    /// <summary>Compatibility writer for item messages whose surrounding envelope is still legacy.</summary>
    internal static Pb Write(ObjectEffect effect)
    {
        var line = Pb.New();
        switch (effect.EffectCase)
        {
            case ObjectEffectCaseNone:
                break;
            case ObjectEffectCaseValueString text:
                line.Str(1, text.Value);
                break;
            case ObjectEffectCaseValueInt number:
                line.Var(4, number.Value);
                break;
            case ObjectEffectCaseValueMinMax range:
                line.Msg(5, Pb.New().VarIfNotZero(1, range.Value.Max).VarIfNotZero(2, range.Value.Min));
                break;
            case ObjectEffectCaseValueDice dice:
                line.Msg(6, Pb.New().VarIfNotZero(1, dice.Value.Const)
                    .VarIfNotZero(2, dice.Value.Num).VarIfNotZero(3, dice.Value.Side));
                break;
            case ObjectEffectCaseValueDate date:
                var when = date.Value;
                line.Msg(2, Pb.New().VarIfNotZero(1, when.Day).VarIfNotZero(2, when.Year)
                    .VarIfNotZero(3, when.Month).VarIfNotZero(4, when.Minute).VarIfNotZero(5, when.Hour));
                break;
            default:
                throw new ArgumentException("Unsupported item effect value.", nameof(effect));
        }
        return line.Var(11, effect.Action);
    }
}
