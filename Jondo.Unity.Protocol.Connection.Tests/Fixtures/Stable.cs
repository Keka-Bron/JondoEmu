namespace Jondo.Unity.Protocol.Connection;

// Synthetic, hand-written stable types used to compile and execute generator output.
public abstract record FixtureMessage;
public sealed record FixturePayload(int? Count, IReadOnlyList<long> Ids, FixtureMode State,
    FixtureEmpty? Child, FixtureEmpty RequiredChild) : FixtureMessage;
public sealed record FixtureEmpty : FixtureMessage;
public enum FixtureMode { Allowed = 100, Default = 200 }
