namespace Jondo.Unity.ServerMods;

/// <summary>Why a character did not share a quest step.</summary>
public enum QuestLagReason
{
    /// <summary>On a different map from the quest's initiating character.</summary>
    Elsewhere = 1,
    /// <summary>One of the characters was in a fight.</summary>
    InFight = 2,
    /// <summary>Not among the winners of the fight that ticked it.</summary>
    NotInTheFight = 3,
    /// <summary>Without the items the objective takes.</summary>
    MissingItems = 4,
    /// <summary>The reply that did it is not shared: it takes items or kamas, or moves the initiating character.</summary>
    NotShared = 5,
    /// <summary>The character has not started the quest.</summary>
    NotStarted = 6,
    /// <summary>The character was on a different step.</summary>
    Behind = 7,
    /// <summary>The character does not meet the quest's requirements.</summary>
    Requirements = 8,
}

/// <summary>The reason for skipping progress and any missing item and quantity.</summary>
public sealed record LagCause(QuestLagReason Reason, int ItemId = 0, int ItemCount = 0);

/// <summary>A quest with progress the character did not share.</summary>
public sealed record QuestLag(int QuestId, int StepId, QuestLagReason Reason, int ItemId, int ItemCount);


[Flags]
public enum QuestReplyEffects
{
    None = 0,
    Purchase = 1,
    RemovesItems = 2,
    Marks = 4,
    DungeonDoor = 8,
    ReturnsHome = 16,
    Phoenix = 32,
    Mutation = 64,
}
