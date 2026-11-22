using System.Text.Json.Serialization;

namespace Jondo.Unity.Protocol.Game;

public sealed record AttackMonsterRequest(long MonsterGroupId) : GameRequest;
public sealed record GameActionFightCastOnTargetRequest(long TargetId, int SpellId) : GameRequest;
public sealed record FightMarkedCell(int Id);
public sealed record FightNewWaveEvent(Team Team, int WaveId, int TurnLeftBeforeNextWave) : GameEvent;
public sealed record ApplySpellModifierEvent(SpellModifier? Modifier, long ActorId) : GameEvent;
public sealed record RemoveSpellModifierEvent(SpellModifierType ModifierType, SpellModifierActionType ActionType, int SpellId, long ActorId) : GameEvent;

public sealed record FightJoinRequest(long FighterId, int FightId) : GameRequest;
public sealed record FightSpectatePlayerRequest(long PlayerId) : GameRequest;
public sealed record FightChallengeJoinRefuseEvent(FighterRefusedReason Reason) : GameEvent;
public sealed record FightLeaveEvent(long CharacterId) : GameEvent;
public sealed record FightEndKickedEvent() : GameEvent;
public sealed record MapRunningFightsRequest() : GameRequest;
public sealed record MapRunningFightDetailsRequest(int FightId) : GameRequest;
public sealed record FightMapRequest() : GameRequest;
public sealed record FightMapInformationResponse(long FightMapId, long MapId, FightStartingPositions? FightStartPositions) : GameResponse;
public sealed record FightOptionToggleRequest(FightOption Option) : GameRequest;
public sealed record FightPlacementSwapPositionsAcceptRequest(int RequestId) : GameRequest;
public sealed record FightPlacementSwapPositionsCancelRequest(int RequestId) : GameRequest;
public sealed record FightPlacementSwapPositionsCancelledEvent(long CancellerId, int RequestId) : GameEvent;
public sealed record FightPlacementSwapPositionsErrorEvent() : GameEvent;
public sealed record FightPlacementSwapPositionsOfferEvent(long TargetId, int RequestId, long RequesterId) : GameEvent;
public sealed record ShowCellRequest(int CellId) : GameRequest;
public sealed record ShowCellEvent(long SourceId, int CellId) : GameEvent;

public enum FighterRefusedReason
{
    FighterRefused, FighterAccepted, ChallengeFull, TeamFull, WrongAlignment, WrongGuild, TooLate, MutantRefused,
    WrongMap, JustRespawned, ImOccupied, OpponentOccupied, FightMyself, InsufficientRights, MemberAccountNeeded,
    OpponentNotMember, TeamLimitedByMainCharacter, MultiAccountNotAllowed, GhostRefused, WrongAlliance, AvaZone,
    EntityRefused, NotEnoughRoom, GuestAccount
}

public sealed record FightAutoJoinActivationRequest() : GameRequest;
public sealed record FightAutoJoinActivationResponse() : GameResponse;
public sealed record FightAutoJoinDeactivationRequest() : GameRequest;
public sealed record FightAutoJoinDeactivationResponse() : GameResponse;
public sealed record FightAutoReadyActivationRequest() : GameRequest;
public sealed record FightAutoReadyActivationResponse() : GameResponse;
public sealed record FightAutoReadyDeactivationRequest() : GameRequest;
public sealed record FightAutoReadyDeactivationResponse() : GameResponse;

public enum ChallengeBonus { Experience, Drop }
public sealed record ChallengeAddEvent(ChallengeInformation? Challenge) : GameEvent;
public sealed record ChallengeBonusChoiceRequest(ChallengeBonus ChallengeBonus) : GameRequest;
public sealed record ChallengeBonusChoiceSelectedEvent(ChallengeBonus ChallengeBonus) : GameEvent;
public sealed record ChallengeModSelectRequest(ChallengeMod ChallengeMod) : GameRequest;
public sealed record ChallengeProposalEvent(long Timer, IReadOnlyList<ChallengeInformation> ChallengeProposals) : GameEvent;
public sealed record ChallengeReadyRequest() : GameRequest;
public sealed record ChallengeResultEvent(int ChallengeId, bool Success) : GameEvent;
public sealed record ChallengeSelectionRequest(int ChallengeId) : GameRequest;
public sealed record ChallengeTargetsEvent(ChallengeInformation? Challenge) : GameEvent;
public sealed record ChallengeTargetsRequest(int ChallengeId) : GameRequest;
public sealed record ChallengeValidateRequest(int ChallengeId) : GameRequest;

public sealed record Choice(int Id, int Position);
public sealed record ChoiceSelectedRequest(int Position) : GameRequest;
public sealed record ChoiceSelectionEvent(IReadOnlyList<Choice> Choices, bool IsCancellable, int SelectionId) : GameEvent;

public enum ArenaRegistrationStep { Registered, WaitingFight, StartingFight, Unregistered }
public sealed record ArenaFightAnswerRequest(bool Accept) : GameRequest;
public sealed record ArenaFightAnswerResponse(bool Acknowledged) : GameResponse;
public sealed record ArenaFightPropositionEvent(int FightId, int Duration, IReadOnlyList<long> Allies) : GameEvent;
public sealed record ArenaFightStatisticsAcknowledgementRequest() : GameRequest;
public sealed record ArenaFighterStatusEvent(long CharacterId, int FightId, bool Accepted) : GameEvent;
public sealed record ArenaRegisterRequest(ArenaType ArenaType) : GameRequest;
public sealed record ArenaRegistrationStatusEvent(bool Registered, ArenaRegistrationStep Step) : GameEvent;
public sealed record ArenaStateModeRequest() : GameRequest;
public sealed record ArenaUnregisterRequest() : GameRequest;
public sealed record ReadyToLeaveArenaResponse() : GameResponse;

public sealed record SurrenderInfoRequest() : GameRequest;
public sealed record SurrenderInfoResponse(bool HasSanction, SurrenderAvailability? SurrenderResponse,
    SurrenderVoteAvailability? SurrenderVoteResponse) : GameResponse;
public sealed record SurrenderAvailability(SurrenderAvailabilityCase ResponseCase);
[JsonDerivedType(typeof(SurrenderAvailabilityCaseNone), "None")]
[JsonDerivedType(typeof(SurrenderAvailabilityCaseBeforeTurn), "BeforeTurn")]
public abstract record SurrenderAvailabilityCase;
public sealed record SurrenderAvailabilityCaseNone() : SurrenderAvailabilityCase;
public sealed record SurrenderAvailabilityCaseBeforeTurn(SurrenderBeforeTurn Value) : SurrenderAvailabilityCase;
public sealed record SurrenderBeforeTurn(int MinTurnForSurrender);

public sealed record SurrenderVoteAvailability(SurrenderVoteAvailabilityCase ResponseCase);
[JsonDerivedType(typeof(SurrenderVoteAvailabilityCaseNone), "None")]
[JsonDerivedType(typeof(SurrenderVoteAvailabilityCaseWaiting), "Waiting")]
[JsonDerivedType(typeof(SurrenderVoteAvailabilityCaseBeforeTurn), "BeforeTurn")]
public abstract record SurrenderVoteAvailabilityCase;
public sealed record SurrenderVoteAvailabilityCaseNone() : SurrenderVoteAvailabilityCase;
public sealed record SurrenderVoteAvailabilityCaseWaiting(SurrenderVoteWaiting Value) : SurrenderVoteAvailabilityCase;
public sealed record SurrenderVoteAvailabilityCaseBeforeTurn(SurrenderVoteBeforeTurn Value) : SurrenderVoteAvailabilityCase;
public sealed record SurrenderVoteBeforeTurn(int MinTurnForSurrenderVote);
public sealed record SurrenderVoteWaiting(string VoteUnlockTimeStamp);
public sealed record SurrenderVoteCastRequest(bool Vote) : GameRequest;
public sealed record SurrenderVoteEndEvent(bool VoteResult) : GameEvent;
public sealed record SurrenderVoteStartEvent(int ParticipantNumber, int CastedVoteNumber, int VoteDuration) : GameEvent;
public sealed record SurrenderVoteUpdateEvent() : GameEvent;
public sealed record OpponentSurrenderEvent() : GameEvent;

public enum SocialFightType { TaxCollector, Prism }
public enum FightPhase { Started, JoiningPhase, PositioningPhase }
public sealed record FightPhaseInfo(long PhaseEndTimestamp, FightPhase Phase);
public sealed record SocialFightInformation(SocialFightType FightType, long MapId, int FightId);
public sealed record SocialFight(IReadOnlyList<Character> Attackers, IReadOnlyList<Character> Defenders,
    FightPhaseInfo? Phase, SocialFightInformation? SocialFightInfo);

public sealed record AllianceFightsInformationEvent(IReadOnlyList<SocialFight> AllianceFights) : GameEvent;
public sealed record AllianceFightStartedEvent(SocialFightInformation? FightInformation, FightPhaseInfo? Phase) : GameEvent;
public sealed record AllianceFightFinishedEvent(SocialFightInformation? FightInformation) : GameEvent;
public sealed record AllianceFightPhaseUpdateEvent(SocialFightInformation? FightInformation, FightPhaseInfo? Phase) : GameEvent;
public sealed record AllianceFightFighterAddedEvent(Character? Fighter, SocialFightInformation? FightInformation, Team Team) : GameEvent;
public sealed record AllianceFightFighterRemovedEvent(long FighterId, SocialFightInformation? FightInformation) : GameEvent;
public sealed record AllianceFightJoinRequest(SocialFightInformation? FightInformation) : GameRequest;
public sealed record AllianceFightLeaveRequest(SocialFightInformation? FightInformation) : GameRequest;
public sealed record AllianceFightListenStartRequest() : GameRequest;
public sealed record AllianceFightListenStopRequest() : GameRequest;
public sealed record AllianceFightReplaceRequest(SocialFightInformation? FightInformation, long ToReplaceFighterId) : GameRequest;

public sealed record TaxCollectorAttackResultEvent(MapExtendedCoordinates? Coordinates, bool Dead, int FirstNameId, int LastNameId) : GameEvent;
public sealed record TaxCollectorAttackedEvent(MapExtendedCoordinates? Coordinates, int FirstNameId, int LastNameId) : GameEvent;
public sealed record TaxCollectorFightRequest() : GameRequest;
public sealed record TaxCollectorStateUpdateEvent(string TaxCollectorUid, TaxCollectorState State) : GameEvent;
public enum PrismAttackResult { DefendersWin, AttackersWin }
public sealed record PrismAttackRequest() : GameRequest;
public sealed record PrismAttackResultEvent(PrismAttackResult PrismAttackResult, PrismLocalizedInformation? PrismLocalizedInformation) : GameEvent;
public sealed record PrismAttackedEvent(PrismLocalizedInformation? PrismLocalizedInformation) : GameEvent;
public sealed record PrismRemoveEvent(int SubAreaId) : GameEvent;
public sealed record KothEndEvent(AllianceInformation? Winner) : GameEvent;

public sealed record AlterationRemovedEvent(Alteration? Alteration) : GameEvent;
public sealed record ObjectModifiedEvent(ObjectItemInventory? Object) : GameEvent;
public sealed record InfiniteDreamEndEvent(InfiniteDreamEndEventCase Outcome) : GameEvent;
[JsonDerivedType(typeof(InfiniteDreamEndEventCaseNone), "None")]
[JsonDerivedType(typeof(InfiniteDreamEndEventCaseWin), "Win")]
[JsonDerivedType(typeof(InfiniteDreamEndEventCaseLoss), "Loss")]
public abstract record InfiniteDreamEndEventCase;
public sealed record InfiniteDreamEndEventCaseNone() : InfiniteDreamEndEventCase;
public sealed record InfiniteDreamEndEventCaseWin(InfiniteDreamWinResult Value) : InfiniteDreamEndEventCase;
public sealed record InfiniteDreamEndEventCaseLoss(InfiniteDreamLossResult Value) : InfiniteDreamEndEventCase;
public sealed record InfiniteDreamLossResult();
public sealed record InfiniteDreamWinResult();
