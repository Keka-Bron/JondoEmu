namespace Jondo.Unity.Protocol.Connection;

// Synthetic, hand-written stable types used to compile and execute generator output.
public abstract record FixtureMessage;
public sealed record FixturePayload(int? Count, IReadOnlyList<long> Ids, FixtureMode State,
    FixtureEmpty? Child, FixtureEmpty RequiredChild) : FixtureMessage;
public sealed record FixtureEmpty : FixtureMessage;
public enum FixtureMode { Allowed = 100, Default = 200 }

public sealed record FixtureAdvanced(byte[] Data, string? Title, byte[]? Blob, FixtureMode? State,
    IReadOnlyDictionary<int, FixtureDefaults> Children, FixtureDefaults Defaulted, FixtureChoice Choice, string Ignored);
public sealed record FixtureDefaults(int Count, string Text);
public abstract record FixtureChoice;
public sealed record FixtureChoiceNone : FixtureChoice;
public sealed record FixtureChoiceNumber(long Value) : FixtureChoice;
public sealed record FixtureChoiceEmpty(FixtureEmpty Value) : FixtureChoice;
