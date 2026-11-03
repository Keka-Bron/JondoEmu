// JondoEmu's stable semantic Game API, maintained independently of wire generation.
using System.Text.Json.Serialization;

namespace Jondo.Unity.Protocol.Game;

public sealed record AccountCapabilitiesEvent(CharacterCreationRights AccountType, bool IsSubscribed, Hierarchy SubscriptionType) : GameEvent;

public sealed record AccountRights(bool ShowConsole, bool UnlimitedAccess, bool InfiniteSubscription, bool UnknownFlag);

public sealed record AccountRightsEvent(AccountRights Rights) : GameEvent;

public sealed record AccountSubscriptionElapsedDurationUpdateEvent() : GameEvent;

public sealed record ActorInformation(EntityLook Look, ActorInformationCase ActorCase);

[JsonDerivedType(typeof(ActorInformationCaseRolePlayActor), "RolePlayActor")]
[JsonDerivedType(typeof(ActorInformationCaseFightActor), "FightActor")]
public abstract record ActorInformationCase;
public sealed record ActorInformationCaseRolePlayActor(RolePlayActor Value) : ActorInformationCase;
public sealed record ActorInformationCaseFightActor(FightFighterInformation Value) : ActorInformationCase;

public sealed record ActorPositionInformation(EntityDisposition Disposition, ActorInformation? ActorInformation, long ActorId);

public enum Alignment { Unknown, Without, Neutral, Angel, Evil }

public sealed record AlignmentExtendedInformation(int Honor, AttackableStatus Attackable);

public sealed record AlignmentInformation(int AlignmentQuestNumber, Alignment Alignment, AlignmentExtendedInformation? ExtendedInformation, int AlignmentGrade, int CharacterLevel);

public sealed record AllianceInformation(string Name);

public sealed record Alteration(long CreationTime, long AlterationId, IReadOnlyList<ObjectEffect> Effects, AlterationExpirationType ExpirationType, long ExpirationValue);

public enum AlterationExpirationType { Unknown, Infinite, Date, FightCount, FightsWonCount, FightsLostCount }

public sealed record AlterationsEvent(IReadOnlyList<Alteration> Alterations) : GameEvent;

public sealed record AlterationsUpdateEvent(IReadOnlyList<Alteration> Alterations) : GameEvent;

public sealed record AnomalySubareaInformationRequest() : GameRequest;

public sealed record AreaFightModifierUpdateEvent(IReadOnlyList<int> SpellPairs) : GameEvent;

public enum AttackableStatus { NonAttackable, PvpEnabledAttackable, PvpEnabledNonAttackable, AvaEnabledAttackable, AvaEnabledNonAttackable, AvaDisqualified, AvaPrequalifiedAttackable }

public sealed record AuthenticationTicketAcceptedEvent() : GameEvent;

public sealed record BakApiKeyEvent(string Token) : GameEvent;

public sealed record BakApiKeyTwinEvent(string Token) : GameEvent;

public sealed record BakApiTokenRequest() : GameRequest;

public sealed record BaseFightStatistics(int KillCount, int BlockedDamage, int DamageTaken, int HealDone, int DamageDone, int HealTaken, int AppliedShield);

public sealed record BidExchangerObject(int ObjectUid, IReadOnlyList<ObjectEffect> Effects, int ObjectGid, IReadOnlyList<long> Prices);

public sealed record ChannelUpdateEvent(bool Enable, ChatChannel Channel) : GameEvent;

public sealed record Character(long Id, CharacterComplementCase ComplementCase);

public sealed record CharacterBaseInformation(Gender Gender, CharacterBaseInformationComplementCase ComplementCase);

[JsonDerivedType(typeof(CharacterBaseInformationComplementCaseNone), "None")]
[JsonDerivedType(typeof(CharacterBaseInformationComplementCasePartyMemberInformation), "PartyMemberInformation")]
[JsonDerivedType(typeof(CharacterBaseInformationComplementCaseEpicInformation), "EpicInformation")]
[JsonDerivedType(typeof(CharacterBaseInformationComplementCasePartyInvitationMemberInformation), "PartyInvitationMemberInformation")]
public abstract record CharacterBaseInformationComplementCase;
public sealed record CharacterBaseInformationComplementCaseNone() : CharacterBaseInformationComplementCase;
public sealed record CharacterBaseInformationComplementCasePartyMemberInformation(PartyMemberInformation Value) : CharacterBaseInformationComplementCase;
public sealed record CharacterBaseInformationComplementCaseEpicInformation(CharacterEpicInformation Value) : CharacterBaseInformationComplementCase;
public sealed record CharacterBaseInformationComplementCasePartyInvitationMemberInformation(PartyInvitationMemberInformation Value) : CharacterBaseInformationComplementCase;

public sealed record CharacterBasicInformation(string Name, int? Level, CharacterBasicInformationComplementCase ComplementCase);

[JsonDerivedType(typeof(CharacterBasicInformationComplementCaseCharacterLook), "CharacterLook")]
[JsonDerivedType(typeof(CharacterBasicInformationComplementCaseRank), "Rank")]
[JsonDerivedType(typeof(CharacterBasicInformationComplementCaseSocialMember), "SocialMember")]
public abstract record CharacterBasicInformationComplementCase;
public sealed record CharacterBasicInformationComplementCaseCharacterLook(CharacterLook Value) : CharacterBasicInformationComplementCase;
public sealed record CharacterBasicInformationComplementCaseRank(RankInformation Value) : CharacterBasicInformationComplementCase;
public sealed record CharacterBasicInformationComplementCaseSocialMember(SocialMember Value) : CharacterBasicInformationComplementCase;

public sealed record CharacterCharacteristic(int CharacteristicId, CharacterCharacteristicCase CharacteristicCase);

[JsonDerivedType(typeof(CharacterCharacteristicCaseValue), "Value")]
[JsonDerivedType(typeof(CharacterCharacteristicCaseDetailed), "Detailed")]
[JsonDerivedType(typeof(CharacterCharacteristicCaseUsable), "Usable")]
public abstract record CharacterCharacteristicCase;
public sealed record CharacterCharacteristicCaseValue(CharacterCharacteristicValue Value) : CharacterCharacteristicCase;
public sealed record CharacterCharacteristicCaseDetailed(CharacterCharacteristicDetailed Value) : CharacterCharacteristicCase;
public sealed record CharacterCharacteristicCaseUsable(CharacterCharacteristicDetailedUsable Value) : CharacterCharacteristicCase;

public sealed record CharacterCharacteristicDetailed(int Base, int Additional, int Temporary, int Alignment, int Equipment, int Context);

public sealed record CharacterCharacteristicDetailedUsable(int Base, int Context, int Equipment, int Used, int Temporary, int Additional, int Alignment);

public sealed record CharacterCharacteristicUpgradeRequest(int Intelligence, int Chance, int Vitality, int Wisdom, int Agility, int Strength) : GameRequest;

public sealed record CharacterCharacteristicValue(long Total);

public sealed record CharacterCharacteristics(long ExperienceNextLevelFloor, IReadOnlyList<GlobalSpellModifier> GlobalSpellModifiers, long ExperienceBonusLimit, int CriticalHitWeapon, IReadOnlyList<SpellModifier> SpellModifiers, long ExperienceLevelFloor, long Experience, AlignmentInformation AlignmentInformation, long Kamas, IReadOnlyList<CharacterCharacteristic> Characteristics);

public sealed record CharacterCharacteristicsEvent(CharacterCharacteristics Stats) : GameEvent;

[JsonDerivedType(typeof(CharacterComplementCaseCharacterBasicInformation), "CharacterBasicInformation")]
[JsonDerivedType(typeof(CharacterComplementCaseCharacterRemodelingInformation), "CharacterRemodelingInformation")]
public abstract record CharacterComplementCase;
public sealed record CharacterComplementCaseCharacterBasicInformation(CharacterBasicInformation Value) : CharacterComplementCase;
public sealed record CharacterComplementCaseCharacterRemodelingInformation(CharacterRemodelingInformation Value) : CharacterComplementCase;

public sealed record CharacterCreationRequest(CharacterRemodelingInformation ModelingInformation) : GameRequest;

public enum CharacterCreationResult { CreateOk, CreateErrorNoReason, CreateErrorInvalidName, CreateErrorTooManyCharacters, CreateErrorNotAllowed, CreateErrorNewPlayerNotAllowed, Fallback6, Fallback7, Fallback8 }

public sealed record CharacterCreationResultEvent(NameCompliance Reason, CharacterCreationResult Result) : GameEvent;

public enum CharacterCreationRights { Allowed, TooManyCharacters, CreationDisabled }

public sealed record CharacterEpicInformation(int DeathCount, EpicDeathState DeathState, int DeathMaxLevel);

public sealed record CharacterFirstSelectionRequest(bool DoTutorial, long CharacterId) : GameRequest;

public sealed record CharacterLevelUpEvent(int NewLevel) : GameEvent;

public sealed record CharacterListEvent(IReadOnlyList<Character> Characters) : GameEvent;

public sealed record CharacterListRequest() : GameRequest;

public sealed record CharacterLook(EntityLook Look, int BreedId, CharacterLookComplementCase ComplementCase);

[JsonDerivedType(typeof(CharacterLookComplementCaseBaseInformation), "BaseInformation")]
[JsonDerivedType(typeof(CharacterLookComplementCaseGuild), "Guild")]
[JsonDerivedType(typeof(CharacterLookComplementCaseGrade), "Grade")]
public abstract record CharacterLookComplementCase;
public sealed record CharacterLookComplementCaseBaseInformation(CharacterBaseInformation Value) : CharacterLookComplementCase;
public sealed record CharacterLookComplementCaseGuild(GuildInformation Value) : CharacterLookComplementCase;
public sealed record CharacterLookComplementCaseGrade(AllianceInformation Value) : CharacterLookComplementCase;

public sealed record CharacterNameSuggestionEvent(string Suggestion) : GameEvent;

public sealed record CharacterNameSuggestionRequest() : GameRequest;

public sealed record CharacterRemodelingInformation(string Name, int Head, IReadOnlyList<int> Colors, CharacterToRemodelInformation CharacterToRemodelInformation, int BodyId, Gender Gender, int BreedId);

public sealed record CharacterSelectionError();

[JsonDerivedType(typeof(CharacterSelectionEventCaseSuccess), "Success")]
[JsonDerivedType(typeof(CharacterSelectionEventCaseError), "Error")]
public abstract record CharacterSelectionEvent : GameEvent;
public sealed record CharacterSelectionEventCaseSuccess(CharacterSelectionSuccess Value) : CharacterSelectionEvent;
public sealed record CharacterSelectionEventCaseError(CharacterSelectionError Value) : CharacterSelectionEvent;

public sealed record CharacterSelectionRequest(long CharacterId) : GameRequest;

public sealed record CharacterSelectionSuccess(Character Character);

public enum CharacterState { NotConnected, InRolePlay, InFight, UnknownState }

public sealed record CharacterStatus(CharacterStatusStatus Status, string Message);

public enum CharacterStatusStatus { Offline, Available, Idle, Afk, Private, Solo, Unknown }

public sealed record CharacterToRemodelInformation(int MandatoryChangeMask, int PossibleChangeMask);

public enum ChatChannel { General, Team, Guild, Alliance, Group, Trade, Recruitment, Noob, Admin, Private, Information, Fight, Promotional, Arena, Community, Unknown15, Unknown16, Unknown17, Unknown18, Unknown19 }

public sealed record ChatChannelMessageEvent(int? OriginServerId, string SenderPrefix, string Date, string SenderName, long SenderCharacterId, long SenderAccountId, string Content, ChatChannel Channel, bool FromAdmin) : GameEvent;

public sealed record ChatChannelMessageRequest(string Content, ChatChannel Channel) : GameRequest;

public sealed record ChatChannelSubscriptionRequest(IReadOnlyList<ChatChannel> ActiveChannels, IReadOnlyList<ChatChannel> DisabledChannels) : GameRequest;

public sealed record ChatCommunitySelectionRequest(int Community) : GameRequest;

public sealed record ContactWarnOnAchievementCompleteSetRequest(bool Enable) : GameRequest;

public sealed record ContactWarnOnPermanentDeathSetRequest(bool Enable) : GameRequest;

public sealed record ContextCreationEvent(GameContext Context) : GameEvent;

public sealed record ContextCreationRequest() : GameRequest;

public sealed record ContextDestroyEvent() : GameEvent;

public sealed record ContextQuitRequest() : GameRequest;

public sealed record ContextReadyRequest(long MapId) : GameRequest;

public sealed record ContextRemoveElementEvent(long ElementId) : GameEvent;

public enum CraftResult { Impossible, Failed, Success, Neutral, Forbidden }

public enum DelayedActionType { Disconnect, ObjectUse, JoinCharacter, AggressionImmune }

public sealed record DialogLeaveRequest() : GameRequest;

public enum DialogType { DialogBook, DialogDialog, DialogLockable, DialogPurchasable, DialogGuildInvitation, DialogGuildCreate, DialogGuildRename, DialogMarriage, DialogDungeonMeeting, DialogSpellForget, DialogTeleporter, DialogExchange, DialogAllianceInvitation, DialogAllianceCreate, DialogAllianceRename, DialogHavenbagMeeting }

public enum Direction { East, SouthEast, South, SouthWest, West, NorthWest, North, NorthEast }

public enum EffectDissipationState { Dissipated, DissipatedByDeath, DissipatedByStrongDispel, ReallyNotDissipated }

public sealed record EntitiesDispositionEvent(IReadOnlyList<EntityDisposition> Added) : GameEvent;

public sealed record EntityDisposition(int? CellId, Direction Direction, long? EntityId, long? CarryingCharacterId);

public sealed record EntityFighterInformation(long MasterId, string MasterName, int EntityModelId, int Level);

public sealed record EntityLook(IReadOnlyList<int> Colors, int LookType, int Bones, IReadOnlyList<int> Scales, IReadOnlyList<int> Skins);

public sealed record EntitySpawnCharacter();

public sealed record EntitySpawnCompanion();

public sealed record EntitySpawnInformation(EntitySpawnInformationCase SpawnInformationCase);

[JsonDerivedType(typeof(EntitySpawnInformationCaseCompanion), "Companion")]
[JsonDerivedType(typeof(EntitySpawnInformationCaseCharacter), "Character")]
[JsonDerivedType(typeof(EntitySpawnInformationCaseMonster), "Monster")]
public abstract record EntitySpawnInformationCase;
public sealed record EntitySpawnInformationCaseCompanion(EntitySpawnCompanion Value) : EntitySpawnInformationCase;
public sealed record EntitySpawnInformationCaseCharacter(EntitySpawnCharacter Value) : EntitySpawnInformationCase;
public sealed record EntitySpawnInformationCaseMonster(EntitySpawnMonsterInformation Value) : EntitySpawnInformationCase;

public sealed record EntitySpawnMonsterInformation(int MonsterGid, EntitySpawnMonsterInformationCase InvocationInformationCase);

[JsonDerivedType(typeof(EntitySpawnMonsterInformationCaseLevel), "Level")]
[JsonDerivedType(typeof(EntitySpawnMonsterInformationCaseGrade), "Grade")]
public abstract record EntitySpawnMonsterInformationCase;
public sealed record EntitySpawnMonsterInformationCaseLevel(int Value) : EntitySpawnMonsterInformationCase;
public sealed record EntitySpawnMonsterInformationCaseGrade(int Value) : EntitySpawnMonsterInformationCase;

public enum EpicDeathState { DeathStateAlive, DeathStateDead, DeathStateWaitingConfirmation }

public sealed record ExchangeBidBuyerStartedEvent(SellingConditions SellingConditions) : GameEvent;

public sealed record ExchangeBidHouseInListUpdatedEvent(IReadOnlyList<long> Prices, int ObjectUid, IReadOnlyList<ObjectEffect> Effects, int ObjectGid, int ObjectType) : GameEvent;

public sealed record ExchangeBidHouseItemListEvent(int ObjectType, int ObjectGid, IReadOnlyList<BidExchangerObject> ItemDescriptions) : GameEvent;

public sealed record ExchangeBoughtEvent() : GameEvent;

public sealed record ExchangeBuyRequest(int ObjectGid, int Quantity) : GameRequest;

public sealed record ExchangeCraftResultEvent(CraftResult Result, ExchangeCraftResultEventComplementCase ComplementCase) : GameEvent;

[JsonDerivedType(typeof(ExchangeCraftResultEventComplementCaseObjectGid), "ObjectGid")]
[JsonDerivedType(typeof(ExchangeCraftResultEventComplementCaseObject), "Object")]
public abstract record ExchangeCraftResultEventComplementCase;
public sealed record ExchangeCraftResultEventComplementCaseObjectGid(int Value) : ExchangeCraftResultEventComplementCase;
public sealed record ExchangeCraftResultEventComplementCaseObject(ExchangeCraftResultWithObjectDescription Value) : ExchangeCraftResultEventComplementCase;

public sealed record ExchangeCraftResultWithObjectDescription(MagicPoolStatus? MagicPoolStatus, float FmPower, ObjectItem Object);

public sealed record ExchangeCraftStartedEvent(int? SkillId) : GameEvent;

public sealed record ExchangeLeaveEvent(bool Success, DialogType DialogType) : GameEvent;

public sealed record ExchangeNpcShopStartedEvent(IReadOnlyList<ItemMinimalInformation> Objects, long SellerId) : GameEvent;

public sealed record ExchangeObjectRemovedEvent(int ObjectUid, bool Remote) : GameEvent;

public sealed record ExchangeObjectRemovedFromBagEvent(int ObjectUid, bool Remote) : GameEvent;

public sealed record ExchangeObjectsAddedEvent(IReadOnlyList<ObjectItemInventory> Objects, bool Remote, float FmPower) : GameEvent;

public sealed record FightAIFighterInformation(FightAIFighterInformationCase FighterCase);

[JsonDerivedType(typeof(FightAIFighterInformationCaseTaxCollector), "TaxCollector")]
[JsonDerivedType(typeof(FightAIFighterInformationCaseMonster), "Monster")]
public abstract record FightAIFighterInformationCase;
public sealed record FightAIFighterInformationCaseTaxCollector(FightTaxCollectorInformation Value) : FightAIFighterInformationCase;
public sealed record FightAIFighterInformationCaseMonster(FightMonsterInformation Value) : FightAIFighterInformationCase;

[JsonDerivedType(typeof(FightActionCaseSummons), "Summons")]
[JsonDerivedType(typeof(FightActionCaseExchangePositions), "ExchangePositions")]
[JsonDerivedType(typeof(FightActionCaseLifePointsGain), "LifePointsGain")]
[JsonDerivedType(typeof(FightActionCaseTargetedAbility), "TargetedAbility")]
[JsonDerivedType(typeof(FightActionCaseDeath), "Death")]
[JsonDerivedType(typeof(FightActionCaseTriggerGlyphTrap), "TriggerGlyphTrap")]
[JsonDerivedType(typeof(FightActionCaseVanish), "Vanish")]
[JsonDerivedType(typeof(FightActionCaseTackled), "Tackled")]
[JsonDerivedType(typeof(FightActionCaseReduceDamages), "ReduceDamages")]
[JsonDerivedType(typeof(FightActionCaseKill), "Kill")]
[JsonDerivedType(typeof(FightActionCasePlaySequence), "PlaySequence")]
[JsonDerivedType(typeof(FightActionCaseStealKama), "StealKama")]
[JsonDerivedType(typeof(FightActionCaseActivateGlyphTrap), "ActivateGlyphTrap")]
[JsonDerivedType(typeof(FightActionCaseCarryCharacter), "CarryCharacter")]
[JsonDerivedType(typeof(FightActionCaseModifyEffectsDuration), "ModifyEffectsDuration")]
[JsonDerivedType(typeof(FightActionCasePointsVariation), "PointsVariation")]
[JsonDerivedType(typeof(FightActionCaseNoChange), "NoChange")]
[JsonDerivedType(typeof(FightActionCaseUnmarkCells), "UnmarkCells")]
[JsonDerivedType(typeof(FightActionCaseSpellRemove), "SpellRemove")]
[JsonDerivedType(typeof(FightActionCaseThrowCharacter), "ThrowCharacter")]
[JsonDerivedType(typeof(FightActionCaseExecuteScript), "ExecuteScript")]
[JsonDerivedType(typeof(FightActionCaseChangeLook), "ChangeLook")]
[JsonDerivedType(typeof(FightActionCaseDropCharacter), "DropCharacter")]
[JsonDerivedType(typeof(FightActionCaseDodgePointLoss), "DodgePointLoss")]
[JsonDerivedType(typeof(FightActionCaseRemoveHitbox), "RemoveHitbox")]
[JsonDerivedType(typeof(FightActionCaseCreateHitbox), "CreateHitbox")]
[JsonDerivedType(typeof(FightActionCaseReflectSpell), "ReflectSpell")]
[JsonDerivedType(typeof(FightActionCaseMarkCells), "MarkCells")]
[JsonDerivedType(typeof(FightActionCaseDispelSpell), "DispelSpell")]
[JsonDerivedType(typeof(FightActionCaseInvisibility), "Invisibility")]
[JsonDerivedType(typeof(FightActionCaseTeleportOnSameMap), "TeleportOnSameMap")]
[JsonDerivedType(typeof(FightActionCaseInvisibleDetected), "InvisibleDetected")]
[JsonDerivedType(typeof(FightActionCaseReflectDamages), "ReflectDamages")]
[JsonDerivedType(typeof(FightActionCaseSlide), "Slide")]
[JsonDerivedType(typeof(FightActionCaseChangeTeam), "ChangeTeam")]
[JsonDerivedType(typeof(FightActionCaseLifePointsLost), "LifePointsLost")]
public abstract record FightAction;
public sealed record FightActionCaseSummons(FightActionSummons Value) : FightAction;
public sealed record FightActionCaseExchangePositions(FightActionExchangePositions Value) : FightAction;
public sealed record FightActionCaseLifePointsGain(FightActionLifePointsGain Value) : FightAction;
public sealed record FightActionCaseTargetedAbility(FightActionTargetedAbility Value) : FightAction;
public sealed record FightActionCaseDeath(FightActionDeath Value) : FightAction;
public sealed record FightActionCaseTriggerGlyphTrap(FightActionTriggerGlyphTrap Value) : FightAction;
public sealed record FightActionCaseVanish(FightActionVanish Value) : FightAction;
public sealed record FightActionCaseTackled(FightActionTackled Value) : FightAction;
public sealed record FightActionCaseReduceDamages(FightActionReduceDamages Value) : FightAction;
public sealed record FightActionCaseKill(FightActionKill Value) : FightAction;
public sealed record FightActionCasePlaySequence(FightActionPlaySequence Value) : FightAction;
public sealed record FightActionCaseStealKama(FightActionStealKama Value) : FightAction;
public sealed record FightActionCaseActivateGlyphTrap(FightActionActivateGlyphTrap Value) : FightAction;
public sealed record FightActionCaseCarryCharacter(FightActionCarryCharacter Value) : FightAction;
public sealed record FightActionCaseModifyEffectsDuration(FightActionModifyEffectsDuration Value) : FightAction;
public sealed record FightActionCasePointsVariation(FightActionPointsVariation Value) : FightAction;
public sealed record FightActionCaseNoChange(FightActionNoChange Value) : FightAction;
public sealed record FightActionCaseUnmarkCells(FightActionUnmarkCells Value) : FightAction;
public sealed record FightActionCaseSpellRemove(FightActionSpellRemove Value) : FightAction;
public sealed record FightActionCaseThrowCharacter(FightActionThrowCharacter Value) : FightAction;
public sealed record FightActionCaseExecuteScript(FightActionExecuteScript Value) : FightAction;
public sealed record FightActionCaseChangeLook(FightActionChangeLook Value) : FightAction;
public sealed record FightActionCaseDropCharacter(FightActionDropCharacter Value) : FightAction;
public sealed record FightActionCaseDodgePointLoss(FightActionDodgePointLoss Value) : FightAction;
public sealed record FightActionCaseRemoveHitbox(FightActionRemoveHitbox Value) : FightAction;
public sealed record FightActionCaseCreateHitbox(FightActionCreateHitbox Value) : FightAction;
public sealed record FightActionCaseReflectSpell(FightActionReflectSpell Value) : FightAction;
public sealed record FightActionCaseMarkCells(FightActionMarkCells Value) : FightAction;
public sealed record FightActionCaseDispelSpell(FightActionDispelSpell Value) : FightAction;
public sealed record FightActionCaseInvisibility(FightActionInvisibility Value) : FightAction;
public sealed record FightActionCaseTeleportOnSameMap(FightActionTeleportOnSameMap Value) : FightAction;
public sealed record FightActionCaseInvisibleDetected(FightActionInvisibleDetected Value) : FightAction;
public sealed record FightActionCaseReflectDamages(FightActionReflectDamages Value) : FightAction;
public sealed record FightActionCaseSlide(FightActionSlide Value) : FightAction;
public sealed record FightActionCaseChangeTeam(FightActionChangeTeam Value) : FightAction;
public sealed record FightActionCaseLifePointsLost(FightActionLifePointsLost Value) : FightAction;

public sealed record FightActionActivateGlyphTrap(int MarkId, bool Active);

public sealed record FightActionCarryCharacter(int Cell, long TargetId);

public sealed record FightActionChangeLook(long TargetId, EntityLook Look);

public sealed record FightActionChangeTeam(Team Team);

public sealed record FightActionCreateHitbox(int Cell);

public sealed record FightActionDeath(long TargetId);

public sealed record FightActionDispelSpell(int SpellId, int SpellRank, long TargetId, bool VerboseCast);

public sealed record FightActionDodgePointLoss(int Amount, long TargetId);

public sealed record FightActionDropCharacter(long TargetId, int Cell);

public sealed record FightActionExchangePositions(int TargetCellId, long TargetId, int CasterCellId);

public sealed record FightActionExecuteScript(bool Critical, int SpellRank, int Cell, int SpellId, int ScriptUsageId);

public sealed record FightActionInvisibility(FightInvisibilityState InvisibilityState, long TargetId);

public sealed record FightActionInvisibleDetected(int Cell, long TargetId);

public sealed record FightActionKill(long TargetId);

public sealed record FightActionLifePointsGain(int Delta, long TargetId);

public sealed record FightActionLifePointsLost(long TargetId, int Loss, int ElementId, int PermanentDamages);

public sealed record FightActionMarkCells(FightMark Mark);

public sealed record FightActionModifyEffectsDuration(int Delta, long TargetId);

public sealed record FightActionNoChange(long TargetId);

public sealed record FightActionPlaySequence(int IntensityMultiplier, bool IsBlocking, int SequenceId);

public sealed record FightActionPointsVariation(int Delta, long TargetId);

public sealed record FightActionReduceDamages(int Amount, long TargetId);

public sealed record FightActionReflectDamages();

public sealed record FightActionReflectSpell(long TargetId);

public sealed record FightActionRemoveHitbox(int Cell);

public sealed record FightActionSlide(int StartCell, long TargetId, int EndCell);

public sealed record FightActionSpellCast(IReadOnlyList<int> PortalsId, int SpellId, int SpellLevel);

public sealed record FightActionSpellRemove(long TargetId, FightActionSpellRemoveComplementCase ComplementCase);

[JsonDerivedType(typeof(FightActionSpellRemoveComplementCaseBuffId), "BuffId")]
[JsonDerivedType(typeof(FightActionSpellRemoveComplementCaseEffectUid), "EffectUid")]
public abstract record FightActionSpellRemoveComplementCase;
public sealed record FightActionSpellRemoveComplementCaseBuffId(int Value) : FightActionSpellRemoveComplementCase;
public sealed record FightActionSpellRemoveComplementCaseEffectUid(int Value) : FightActionSpellRemoveComplementCase;

public sealed record FightActionStealKama(long TargetId, long Amount);

public sealed record FightActionSummons(FightActionSummonsCase SummonsCase);

public sealed record FightActionSummonsByActor(IReadOnlyList<ActorPositionInformation> Actors);

public sealed record FightActionSummonsByContextInformation(IReadOnlyList<FightSummonContextInformation> Summons);

[JsonDerivedType(typeof(FightActionSummonsCaseSummonsByActor), "SummonsByActor")]
[JsonDerivedType(typeof(FightActionSummonsCaseSummonsByContextInformation), "SummonsByContextInformation")]
public abstract record FightActionSummonsCase;
public sealed record FightActionSummonsCaseSummonsByActor(FightActionSummonsByActor Value) : FightActionSummonsCase;
public sealed record FightActionSummonsCaseSummonsByContextInformation(FightActionSummonsByContextInformation Value) : FightActionSummonsCase;

public sealed record FightActionTackled();

public sealed record FightActionTargetedAbility(bool SilentCast, long TargetId, IReadOnlyList<SpellCastCount> SpellCastCounts, FightSpellCastCritical Critical, int DestinationCell, bool VerboseCast, SpellGlobalCastCount? GlobalCastCount, FightActionTargetedAbilityCase FightCase);

[JsonDerivedType(typeof(FightActionTargetedAbilityCaseSpellCast), "SpellCast")]
[JsonDerivedType(typeof(FightActionTargetedAbilityCaseWeaponGenericId), "WeaponGenericId")]
public abstract record FightActionTargetedAbilityCase;
public sealed record FightActionTargetedAbilityCaseSpellCast(FightActionSpellCast Value) : FightActionTargetedAbilityCase;
public sealed record FightActionTargetedAbilityCaseWeaponGenericId(int Value) : FightActionTargetedAbilityCase;

public sealed record FightActionTeleportOnSameMap(int Cell, long TargetId);

public sealed record FightActionThrowCharacter(long TargetId, int Cell);

public sealed record FightActionTriggerGlyphTrap(int MarkImpactCell, long TriggeringCharacterId, int MarkId);

public sealed record FightActionUnmarkCells(int MarkId);

public sealed record FightActionVanish(long TargetId);

public sealed record FightActorContextEvent(long ActorId, long? ContextId, IReadOnlyList<SpellItem> Spells, IReadOnlyList<Shortcut> Shortcuts) : GameEvent;

public sealed record FightCharacterResult(IReadOnlyList<FightResultAdditionalData> Additional, int Level);

public sealed record FightCharacteristics(long Summoner, FightInvisibilityState InvisibilityState, bool Summoned, IReadOnlyList<CharacterCharacteristic> Characteristics);

public sealed record FightDamageDoneStatistics(int PoisonDamage, int SummonDamage, float DamagePerActionPoint, int Total, int PushDamage, float DamagePerTurn, int ObjectDamage, int ShieldDamage, int DirectDamage, int ErosionDone);

public sealed record FightDamageTakenStatistics(int ObjectDamage, int ErosionTaken, float DamagePerTurn, int SummonDamage, int ShieldDamage, int PushDamage, int PoisonDamage, int Total);

public sealed record FightEffectAddedEvent(FightRemovableEffectExtendedInformation Effect) : GameEvent;

public enum FightEffectKind { Unknown0, Unknown1, State, WorldBossPhase, Unknown4, NoFlags, FightLog, Triggered }

public sealed record FightEffectRemovedEvent(long TargetId, int EffectUid) : GameEvent;

public sealed record FightEffectsRefreshEvent(IReadOnlyList<FightRemovableEffectExtendedInformation> Effects) : GameEvent;

public sealed record FightEndEvent(int? Budget, IReadOnlyList<FightResultListEntry> Results, IReadOnlyList<NamedPartyTeamWithOutcome> NamedPartyTeamsOutcomes, long Duration, int RewardRate, int? Score, int LootShareLimitMalus) : GameEvent;

public sealed record FightExperienceData(long ExperienceFightDelta, long ExperienceNextLevelFloor, int ReRollExperienceMultiplier, bool ShowExperience, long Experience, long ExperienceLevelFloor, bool ShowExperienceFightDelta);

public sealed record FightFighterInformation(long? FighterId, FightCharacteristics FightCharacteristics, SpawnInformation SpawnInformation, FightFighterInformationCase FighterCase);

[JsonDerivedType(typeof(FightFighterInformationCaseAiFighter), "AiFighter")]
[JsonDerivedType(typeof(FightFighterInformationCaseNamedFighter), "NamedFighter")]
[JsonDerivedType(typeof(FightFighterInformationCaseEntityFighter), "EntityFighter")]
public abstract record FightFighterInformationCase;
public sealed record FightFighterInformationCaseAiFighter(FightAIFighterInformation Value) : FightFighterInformationCase;
public sealed record FightFighterInformationCaseNamedFighter(FightNamedFighterInformation Value) : FightFighterInformationCase;
public sealed record FightFighterInformationCaseEntityFighter(EntityFighterInformation Value) : FightFighterInformationCase;

public sealed record FightFighterShowEvent(ActorPositionInformation Information) : GameEvent;

public sealed record FightHealingDoneStatistics(float HealingPerActionPoint, float HealingPerTurn, int SummonHealing, int Total);

public sealed record FightHealingReceivedStatistics(float HealingPerTurn, int Total, int SummonHealing);

public sealed record FightHumanReadyStateEvent(long CharacterId, bool IsReady) : GameEvent;

public enum FightInvisibilityState { Invisible, Detected, Visible }

public sealed record FightIsTurnReadyEvent() : GameEvent;

public sealed record FightJoinEvent(bool IsTeamPhase, bool IsFightStarted, long TimeMaxBeforeFightStart, FightType FightType) : GameEvent;

public sealed record FightKillStatistics(int Total, int EnemiesKilled, int SummonsKilled);

public sealed record FightLoot(long Kamas, IReadOnlyList<FightLootGroup> LootGroups);

public sealed record FightLootGroup(IReadOnlyList<FightLootObject> Objects, IReadOnlyList<FightLootObject> InventoryObjects, int MonsterId);

public sealed record FightLootObject(int PriorityHint, int Quantity, int Id);

public sealed record FightMapInformation(int Param, long MapId, FightMapInformationPositionCase PositionCase) : GameEvent;

[JsonDerivedType(typeof(FightMapInformationPositionCaseAnomaly), "Anomaly")]
[JsonDerivedType(typeof(FightMapInformationPositionCaseCoords), "Coords")]
public abstract record FightMapInformationPositionCase;
public sealed record FightMapInformationPositionCaseAnomaly(MapComplementaryAnomalyInformation Value) : FightMapInformationPositionCase;
public sealed record FightMapInformationPositionCaseCoords(MapCoordinates Value) : FightMapInformationPositionCase;

public sealed record FightMapRequest() : GameRequest;

public sealed record FightMark();

public sealed record FightMonsterInformation(int CreatureGrade, int MonsterGid, int CreatureLevel);

public sealed record FightMutantInformation(int MonsterId, int PowerLevel);

public sealed record FightMutantResult(int Level);

public sealed record FightNamedFighterCharacterInformation(int BreedId, Gender Gender, int Level, AlignmentInformation AlignmentInformation);

public sealed record FightNamedFighterInformation(int Status, string Name, CharacterStatus StatusInformation, FightNamedFighterInformationCharacterCase CharacterCase);

[JsonDerivedType(typeof(FightNamedFighterInformationCharacterCaseCharacter), "Character")]
[JsonDerivedType(typeof(FightNamedFighterInformationCharacterCaseMutantInformation), "MutantInformation")]
public abstract record FightNamedFighterInformationCharacterCase;
public sealed record FightNamedFighterInformationCharacterCaseCharacter(FightNamedFighterCharacterInformation Value) : FightNamedFighterInformationCharacterCase;
public sealed record FightNamedFighterInformationCharacterCaseMutantInformation(FightMutantInformation Value) : FightNamedFighterInformationCharacterCase;

public sealed record FightNewRoundEvent(int TurnOrder, int RoundNumber) : GameEvent;

public enum FightOption { Unknown0, Unknown1, Unknown2, Unknown3 }

public sealed record FightOptionUpdateEvent(Team Team, FightOption Option, bool State, int FightId) : GameEvent;

public enum FightOutcome { ResultLost, ResultDraw, ResultVictory, ResultTax, ResultDefenderGroup }

public sealed record FightPingsEvent(IReadOnlyDictionary<int, int> PingsByCells, IReadOnlyDictionary<long, int> PingsByEntities) : GameEvent;

public sealed record FightPlacementPositionRequest(long EntityId, int CellId) : GameRequest;

public sealed record FightPlacementPossiblePositionsEvent(FightStartingPositions StartingPositions, Team Team) : GameEvent;

public sealed record FightPointStatistics(int Removed, float RemovedPerTurn, float DodgedPerTurn, float SpentPerTurn, int RemovedByTackle, int Dodged, int NotDodged, float NotDodgedPerTurn);

public sealed record FightPvpData(int UnknownResultValue, int ArenaLeagueId);

public sealed record FightReadyRequest(bool IsReady) : GameRequest;

public sealed record FightRefreshCharacterStatsEvent(long FighterId, FightCharacteristics Stats) : GameEvent;

public sealed record FightRemovableEffect(int Param1, long TargetId, int BuffId, int SpellGrade, FightTurn EndTurn, string Trigger, int SpellEffectUid, bool Critical, int Param3, FightTurn StartTurn, int Param2, int SpellId, FightEffectKind Kind, EffectDissipationState DissipationState);

public sealed record FightRemovableEffectExtendedInformation(FightRemovableEffect Effect, long SourceId, int ActionId);

public sealed record FightResultAdditionalData(FightResultAdditionalDataCase AdditionalData);

[JsonDerivedType(typeof(FightResultAdditionalDataCasePvpData), "PvpData")]
[JsonDerivedType(typeof(FightResultAdditionalDataCaseExperienceData), "ExperienceData")]
public abstract record FightResultAdditionalDataCase;
public sealed record FightResultAdditionalDataCasePvpData(FightPvpData Value) : FightResultAdditionalDataCase;
public sealed record FightResultAdditionalDataCaseExperienceData(FightExperienceData Value) : FightResultAdditionalDataCase;

public sealed record FightResultListEntry(FightLoot Rewards, FighterListEntry Fighter, FightOutcome Outcome);

public sealed record FightShieldingAppliedStatistics(int Total, int SummonShielding, float ShieldingPerTurn);

public sealed record FightShieldingReceivedStatistics(int Total, float ShieldingPerTurn, int SummonShielding);

public enum FightSpellCastCritical { Normal, CriticalHit, CriticalFail }

public sealed record FightSpellCooldown(int SpellId, int Cooldown);

public sealed record FightSpellCooldownEvent(IReadOnlyList<FightSpellCooldown> Cooldowns, long CharacterId) : GameEvent;

public sealed record FightStartEvent() : GameEvent;

public sealed record FightStartingEvent(bool ContainBoss, FightType FightType, IReadOnlyList<int> MonsterIds, int FightId, long AttackerId) : GameEvent;

public sealed record FightStartingPositions(IReadOnlyList<int> ChallengersPositions, IReadOnlyList<int> DefendersPositions);

public sealed record FightStatisticsCompanionInformation(FightStatisticsFighterInformation? Owner, int CompanionId);

public sealed record FightStatisticsEvent(IReadOnlyDictionary<long, FighterFightStatistics> StatisticsByFighter, BaseFightStatistics? BaseFightStatistics) : GameEvent;

public sealed record FightStatisticsFighterInformation(Team Team, long FighterId, FightStatisticsFighterKind FighterKind);

[JsonDerivedType(typeof(FightStatisticsFighterKindCasePlayer), "Player")]
[JsonDerivedType(typeof(FightStatisticsFighterKindCaseMonster), "Monster")]
[JsonDerivedType(typeof(FightStatisticsFighterKindCaseCompanion), "Companion")]
[JsonDerivedType(typeof(FightStatisticsFighterKindCaseSummon), "Summon")]
public abstract record FightStatisticsFighterKind;
public sealed record FightStatisticsFighterKindCasePlayer(FightStatisticsPlayerInformation Value) : FightStatisticsFighterKind;
public sealed record FightStatisticsFighterKindCaseMonster(FightStatisticsMonsterInformation Value) : FightStatisticsFighterKind;
public sealed record FightStatisticsFighterKindCaseCompanion(FightStatisticsCompanionInformation Value) : FightStatisticsFighterKind;
public sealed record FightStatisticsFighterKindCaseSummon(FightStatisticsSummonInformation Value) : FightStatisticsFighterKind;

public sealed record FightStatisticsMonsterInformation(int MonsterId);

public sealed record FightStatisticsPlayerInformation();

public sealed record FightStatisticsSummonInformation(FightStatisticsFighterInformation? Owner, int MonsterId);

public sealed record FightSummonContextInformation(IReadOnlyList<SpawnInformation> Summons, EntityLook Look, EntitySpawnInformation SpawnInformation, FightCharacteristics Characteristics);

public sealed record FightSynchronizeEvent(IReadOnlyList<ActorPositionInformation> Fighters) : GameEvent;

public sealed record FightTaxCollectorInformation(int FirstNameId, int LastNameId, int MonsterGid);

public sealed record FightTaxCollectorResult(AllianceInformation AllianceInformation);

public sealed record FightTeamInformation(TeamType TeamType, long LeaderId, Alignment Side, Team Team, int Waves, FightTeamInformationCase TeamInformationCase);

[JsonDerivedType(typeof(FightTeamInformationCaseLightInformation), "LightInformation")]
[JsonDerivedType(typeof(FightTeamInformationCaseMembers), "Members")]
public abstract record FightTeamInformationCase;
public sealed record FightTeamInformationCaseLightInformation(FightTeamLightInformation Value) : FightTeamInformationCase;
public sealed record FightTeamInformationCaseMembers(FightTeamMembersInformation Value) : FightTeamInformationCase;

public sealed record FightTeamLightInformation(bool HasFriend, bool HasAllianceMember, int TeamMembersCount, bool HasMyTaxCollector, bool HasGuildMember, bool HasGroupMember, int AverageLevel);

public sealed record FightTeamMemberCharacterInformation(int BreedOrLevel, string Name);

public sealed record FightTeamMemberEntity(int EntityModelId, int Level, long MasterId);

public sealed record FightTeamMemberInformation(long CharacterId, FightTeamMemberInformationCase MemberCase);

[JsonDerivedType(typeof(FightTeamMemberInformationCaseCharacter), "Character")]
[JsonDerivedType(typeof(FightTeamMemberInformationCaseTaxCollectorMember), "TaxCollectorMember")]
[JsonDerivedType(typeof(FightTeamMemberInformationCaseMonsterMember), "MonsterMember")]
[JsonDerivedType(typeof(FightTeamMemberInformationCaseEntityMember), "EntityMember")]
public abstract record FightTeamMemberInformationCase;
public sealed record FightTeamMemberInformationCaseCharacter(FightTeamMemberCharacterInformation Value) : FightTeamMemberInformationCase;
public sealed record FightTeamMemberInformationCaseTaxCollectorMember(FightTeamMemberTaxCollector Value) : FightTeamMemberInformationCase;
public sealed record FightTeamMemberInformationCaseMonsterMember(FightTeamMemberMonster Value) : FightTeamMemberInformationCase;
public sealed record FightTeamMemberInformationCaseEntityMember(FightTeamMemberEntity Value) : FightTeamMemberInformationCase;

public sealed record FightTeamMemberMonster(int MonsterId, int Grade);

public sealed record FightTeamMemberTaxCollector(int FirstNameId, int LastNameId);

public sealed record FightTeamMembersInformation(IReadOnlyList<FightTeamMemberInformation> Members);

public sealed record FightTeamUpdateEvent(FightTeamInformation Team, int FightId) : GameEvent;

public sealed record FightTurn(int Round, int Order);

public sealed record FightTurnEndEvent(int SavedDeciseconds, long CharacterId) : GameEvent;

[JsonDerivedType(typeof(FightTurnEntityCaseWithAdditionalId), "WithAdditionalId")]
[JsonDerivedType(typeof(FightTurnEntityCaseWithFlag), "WithFlag")]
public abstract record FightTurnEntity;
public sealed record FightTurnEntityCaseWithAdditionalId(FightTurnWithAdditionalId Value) : FightTurnEntity;
public sealed record FightTurnEntityCaseWithFlag(FightTurnWithFlag Value) : FightTurnEntity;

public sealed record FightTurnEvent(long CharacterId, int WaitDeciseconds) : GameEvent;

public sealed record FightTurnFinishRequest(bool IsAfk) : GameRequest;

public sealed record FightTurnListEvent(IReadOnlyList<FightTurnEntity> Turns) : GameEvent;

public sealed record FightTurnReadyRequest(bool IsReady) : GameRequest;

public sealed record FightTurnStartPlayingEvent() : GameEvent;

public sealed record FightTurnWithAdditionalId(long EntityId, string AdditionalTurnId);

public sealed record FightTurnWithFlag(bool UndefinedFlags, long EntityId);

public enum FightType { Challenge, Aggression, PvMa, MxvM, PvM, PvT, PvMu, PvpArena, Koh, PvPr, Breach }

public sealed record FighterFightStatistics(FightStatisticsFighterInformation? FighterInformation, FightKillStatistics? Kills, FightPointStatistics? ActionPoints, FightHealingReceivedStatistics? HealingReceived, FightDamageTakenStatistics? DamageTaken, FightShieldingAppliedStatistics? ShieldingApplied, FightPointStatistics? MovementPoints, FightShieldingReceivedStatistics? ShieldingReceived, FightHealingDoneStatistics? HealingDone, FightDamageDoneStatistics? DamageDone);

public sealed record FighterListEntry(long FighterId, bool Alive, FighterListEntryCase FighterCase);

[JsonDerivedType(typeof(FighterListEntryCaseCharacter), "Character")]
[JsonDerivedType(typeof(FighterListEntryCaseTaxCollector), "TaxCollector")]
[JsonDerivedType(typeof(FighterListEntryCaseMutant), "Mutant")]
public abstract record FighterListEntryCase;
public sealed record FighterListEntryCaseCharacter(FightCharacterResult Value) : FighterListEntryCase;
public sealed record FighterListEntryCaseTaxCollector(FightTaxCollectorResult Value) : FighterListEntryCase;
public sealed record FighterListEntryCaseMutant(FightMutantResult Value) : FighterListEntryCase;

public enum ForgettableSpellAction { Forget, Equip, Remove }

public sealed record ForgettableSpellActionRequest(int SpellId, ForgettableSpellAction Action) : GameRequest;

public sealed record FriendSetStatusShareRequest(bool Share) : GameRequest;

public sealed record FriendSetWarnOnConnectionRequest(bool Enable) : GameRequest;

public sealed record FriendSetWarnOnLevelGainRequest(bool Enable) : GameRequest;

public sealed record GameActionAcknowledgementRequest(bool Valid, int ActionId) : GameRequest;

public sealed record GameActionFightCastRequest(int Cell, int SpellId) : GameRequest;

public sealed record GameActionFightEvent(long SourceId, int ActionType, FightAction Payload) : GameEvent;

public enum GameContext { RolePlay, Fight }

public sealed record GameMapMovementEvent(IReadOnlyList<int> Cells, long CharacterId, bool Cautious) : GameEvent;

public sealed record GameRolePlayShowActorsEvent(IReadOnlyList<ActorPositionInformation> Actors) : GameEvent;

public enum Gender { Male, Female }

public sealed record GlobalSpellModifier(int Context, SpellModifierType ModifierType, SpellModifierActionType ActionType);

public sealed record GuildHallThemeInformation(int ThemeId, IReadOnlyList<int> AvailableThemeIds);

public sealed record GuildInformation(int Id, string Name, int Level);

public sealed record GuildInformationRequest(GuildInformationType InformationType) : GameRequest;

public enum GuildInformationType { InfoGeneral, InfoMembers, InfoBoosts, InfoPaddocks, InfoHouses, InfoRecruitment, InfoLogbook }

public sealed record GuildMember();

public sealed record GuildMemberWarnOnConnectionStartRequest() : GameRequest;

public sealed record GuildMemberWarnOnConnectionStopRequest() : GameRequest;

public enum Hierarchy { RoleUnavailable, RolePlayer, RoleModerator, RoleGameMasterPadawan, RoleGameMaster, RoleAdmin, RoleUnknownSpecialUser }

public sealed record House(HouseInformationCase InformationCase);

[JsonDerivedType(typeof(HouseInformationCaseHouseInside), "HouseInside")]
public abstract record HouseInformationCase;
public sealed record HouseInformationCaseHouseInside(HouseInside Value) : HouseInformationCase;

public sealed record HouseInside();

public sealed record HumanAllianceOption(AttackableStatus AttackableStatus, AllianceInformation AllianceInformation);

public sealed record HumanEmoteOption(int EmoteId);

public sealed record HumanFollowingCharactersOption(IReadOnlyList<HumanIndexedEntityLook> FollowingCharactersLooks);

public sealed record HumanIndexedEntityLook(EntityLook Look, int Index);

public sealed record HumanObjectUseOption(int ObjectGid, DelayedActionType DelayedActionType, string EndDelay);

public sealed record HumanOption(HumanOptionCase OptionCase);

[JsonDerivedType(typeof(HumanOptionCaseEmoteOption), "EmoteOption")]
[JsonDerivedType(typeof(HumanOptionCaseTitleOption), "TitleOption")]
[JsonDerivedType(typeof(HumanOptionCaseObjectUseOption), "ObjectUseOption")]
[JsonDerivedType(typeof(HumanOptionCaseGuildInformation), "GuildInformation")]
[JsonDerivedType(typeof(HumanOptionCaseAllianceInformation), "AllianceInformation")]
[JsonDerivedType(typeof(HumanOptionCaseFollowers), "Followers")]
[JsonDerivedType(typeof(HumanOptionCaseSpeedMultiplier), "SpeedMultiplier")]
[JsonDerivedType(typeof(HumanOptionCaseSkillUseOption), "SkillUseOption")]
[JsonDerivedType(typeof(HumanOptionCaseOrnamentOption), "OrnamentOption")]
public abstract record HumanOptionCase;
public sealed record HumanOptionCaseEmoteOption(HumanEmoteOption Value) : HumanOptionCase;
public sealed record HumanOptionCaseTitleOption(HumanTitleOption Value) : HumanOptionCase;
public sealed record HumanOptionCaseObjectUseOption(HumanObjectUseOption Value) : HumanOptionCase;
public sealed record HumanOptionCaseGuildInformation(GuildInformation Value) : HumanOptionCase;
public sealed record HumanOptionCaseAllianceInformation(HumanAllianceOption Value) : HumanOptionCase;
public sealed record HumanOptionCaseFollowers(HumanFollowingCharactersOption Value) : HumanOptionCase;
public sealed record HumanOptionCaseSpeedMultiplier(int Value) : HumanOptionCase;
public sealed record HumanOptionCaseSkillUseOption(HumanSkillUseOption Value) : HumanOptionCase;
public sealed record HumanOptionCaseOrnamentOption(HumanOrnamentOption Value) : HumanOptionCase;

public sealed record HumanOrnamentOption(int PlayerLevel, int? LeagueId, int? LadderPosition, int? OrnamentId);

public sealed record HumanSkillUseOption(string SkillEndTime, int SkillId, int ElementId);

public sealed record HumanTitleOption(string TitleParameter, int? TitleId);

public sealed record HumanoidInformation(AlignmentInformation AlignmentInformation, MutantInformation? MutantInformation, long AccountId, IReadOnlyList<HumanOption> Options, Hierarchy Role, IReadOnlyList<Restriction> Restrictions, Gender Gender);

public sealed record IdentificationRequest(string TicketKey, string LanguageCode) : GameRequest;

public sealed record InventoryContentEvent(long Kamas, IReadOnlyList<ObjectItemInventory> Objects) : GameEvent;

public enum InventorySourceContext { Unknown0, BidHouse, Craft }

public sealed record InventorySourceSelectionRequest(IReadOnlyList<ObjectStorage> AdditionalSources, InventorySourceContext Context) : GameRequest;

public sealed record InventoryWeightEvent(int InventoryWeight, int WeightMax) : GameEvent;

public sealed record ItemMinimalInformation(int ObjectGid, IReadOnlyList<ObjectEffect> Effects, ItemMinimalInformationComplementCase ComplementCase);

[JsonDerivedType(typeof(ItemMinimalInformationComplementCaseQuantity), "Quantity")]
[JsonDerivedType(typeof(ItemMinimalInformationComplementCaseItemToSellInNpcShop), "ItemToSellInNpcShop")]
public abstract record ItemMinimalInformationComplementCase;
public sealed record ItemMinimalInformationComplementCaseQuantity(int Value) : ItemMinimalInformationComplementCase;
public sealed record ItemMinimalInformationComplementCaseItemToSellInNpcShop(ItemToSellInNpcShop Value) : ItemMinimalInformationComplementCase;

public sealed record ItemToSellInNpcShop(long Price, int Quantity, string BuyCriterion);

public sealed record KamasUpdateEvent(long Quantity) : GameEvent;

public enum MagicPoolStatus { NoChange, Increase, Loss }

public sealed record MapChangeOrientationEvent(long ActorId, Direction Direction) : GameEvent;

public sealed record MapChangeOrientationRequest(Direction Direction) : GameRequest;

public sealed record MapChangeRequest(int MapId, bool AutoPilot) : GameRequest;

public sealed record MapComplementaryAnomalyInformation();

public sealed record MapComplementaryGuildHallInformation(GuildHallThemeInformation? ThemeInformation);

public sealed record MapComplementaryGuildInformation(MapComplementaryGuildInformationCase ComplementaryInformationCase);

[JsonDerivedType(typeof(MapComplementaryGuildInformationCaseGuildHallInformation), "GuildHallInformation")]
[JsonDerivedType(typeof(MapComplementaryGuildInformationCaseUnknownFuur), "UnknownFuur")]
[JsonDerivedType(typeof(MapComplementaryGuildInformationCaseUnknownFuus), "UnknownFuus")]
public abstract record MapComplementaryGuildInformationCase;
public sealed record MapComplementaryGuildInformationCaseGuildHallInformation(MapComplementaryGuildHallInformation Value) : MapComplementaryGuildInformationCase;
public sealed record MapComplementaryGuildInformationCaseUnknownFuur(UnknownJrn Value) : MapComplementaryGuildInformationCase;
public sealed record MapComplementaryGuildInformationCaseUnknownFuus(UnknownJqj Value) : MapComplementaryGuildInformationCase;

public sealed record MapComplementaryHavenBagInformation(int Theme, int RoomId, Character OwnerInformation);

public sealed record MapComplementaryInHouseInformation(House CurrentHouse);

public sealed record MapComplementaryInformationEvent(long MapId, IReadOnlyList<ActorPositionInformation> Actors, int SubareaId, MapSpecificComplementaryInformationCase SpecificCase) : GameEvent;

public sealed record MapCoordinates(int X, int Y);

public sealed record MapCurrentEvent(long MapId) : GameEvent;

public sealed record MapExtendedCoordinates();

public sealed record MapInformationRequest(int MapId) : GameRequest;

public sealed record MapMovementCancelRequest(int CellId) : GameRequest;

public sealed record MapMovementConfirmRequest() : GameRequest;

public sealed record MapMovementConfirmedResponse() : GameResponse;

public sealed record MapMovementRefusedEvent(int CellX, int CellY) : GameEvent;

public sealed record MapMovementRequest(long MapId, IReadOnlyList<int> KeyCells, bool Cautious) : GameRequest;

[JsonDerivedType(typeof(MapSpecificComplementaryInformationCaseAnomaly), "Anomaly")]
[JsonDerivedType(typeof(MapSpecificComplementaryInformationCaseGuildInformation), "GuildInformation")]
[JsonDerivedType(typeof(MapSpecificComplementaryInformationCaseInHouseInformation), "InHouseInformation")]
[JsonDerivedType(typeof(MapSpecificComplementaryInformationCaseHavenBagInformation), "HavenBagInformation")]
[JsonDerivedType(typeof(MapSpecificComplementaryInformationCaseCoords), "Coords")]
public abstract record MapSpecificComplementaryInformationCase;
public sealed record MapSpecificComplementaryInformationCaseAnomaly(MapComplementaryAnomalyInformation Value) : MapSpecificComplementaryInformationCase;
public sealed record MapSpecificComplementaryInformationCaseGuildInformation(MapComplementaryGuildInformation Value) : MapSpecificComplementaryInformationCase;
public sealed record MapSpecificComplementaryInformationCaseInHouseInformation(MapComplementaryInHouseInformation Value) : MapSpecificComplementaryInformationCase;
public sealed record MapSpecificComplementaryInformationCaseHavenBagInformation(MapComplementaryHavenBagInformation Value) : MapSpecificComplementaryInformationCase;
public sealed record MapSpecificComplementaryInformationCaseCoords(MapCoordinates Value) : MapSpecificComplementaryInformationCase;

public sealed record MonsterGroupActor(Alignment Alignment, MonsterGroupStaticInformation Identification, MonsterGroupWaveInformation? WaveInformation, int LootShare);

public sealed record MonsterGroupStaticInformation(IReadOnlyList<MonsterInGroupInformation> Underlings, MonsterInGroupInformation MainCreature, IReadOnlyList<MonstersInGroupAlternativeInformation> Alternatives);

public sealed record MonsterGroupWaveInformation(IReadOnlyList<MonsterGroupStaticInformation> Alternatives, int? WaveCount);

public sealed record MonsterInGroupInformation(int Gid, int Level, EntityLook Look, int Grade);

public sealed record MonstersInGroupAlternativeInformation(IReadOnlyList<MonsterInGroupInformation> Monsters, int PlayerCount);

public sealed record Mount();

public sealed record MutantInformation(int PowerLevel, int MonsterId);

public enum NameCompliance { NameOk, NameErrorServiceUnavailable, NameErrorAlreadyExists, NameErrorBadAlphabet, NameErrorBadLength, NameErrorBadChar, NameErrorInvalidDashPosition, NameErrorNameWithBadCase, NameErrorTooManyConsecutiveIdentical, NameErrorTooManySpecial, NameErrorForbidden, NameErrorReserved }

public sealed record NamedActor(string Name, NamedActorCase ActorCase);

[JsonDerivedType(typeof(NamedActorCaseMountInformation), "MountInformation")]
[JsonDerivedType(typeof(NamedActorCaseHumanoidInformation), "HumanoidInformation")]
public abstract record NamedActorCase;
public sealed record NamedActorCaseMountInformation(Mount Value) : NamedActorCase;
public sealed record NamedActorCaseHumanoidInformation(HumanoidInformation Value) : NamedActorCase;

public sealed record NamedPartyTeam(Team Team, string Name);

public sealed record NamedPartyTeamWithOutcome(NamedPartyTeam NamedPartyTeam, FightOutcome Outcome);

public sealed record NpcQuests(IReadOnlyList<int> QuestsToValid, IReadOnlyList<int> QuestsToStart);

public sealed record NpcStaticInformation(string Name, NpcQuests? Quests, Gender Gender, int SpecialArtworkId, int NpcId, IReadOnlyList<int> Actions);

public sealed record ObjectAddedEvent(ObjectOrigin Origin, ObjectItemInventory Object) : GameEvent;

public sealed record ObjectAveragePrice(int ObjectGid, long AveragePrice);

public sealed record ObjectAveragePricesEvent(IReadOnlyList<ObjectAveragePrice> ObjectsAveragePrices) : GameEvent;

public sealed record ObjectAveragePricesRequest() : GameRequest;

public sealed record ObjectDeleteRequest(ObjectUidWithQuantity Object) : GameRequest;

public sealed record ObjectDeletedEvent(int ObjectUid) : GameEvent;

public sealed record ObjectEffect(int Action, ObjectEffectCase EffectCase);

[JsonDerivedType(typeof(ObjectEffectCaseValueString), "ValueString")]
[JsonDerivedType(typeof(ObjectEffectCaseValueDate), "ValueDate")]
[JsonDerivedType(typeof(ObjectEffectCaseValueDurationMinutes), "ValueDurationMinutes")]
[JsonDerivedType(typeof(ObjectEffectCaseValueInt), "ValueInt")]
[JsonDerivedType(typeof(ObjectEffectCaseValueMinMax), "ValueMinMax")]
[JsonDerivedType(typeof(ObjectEffectCaseValueDice), "ValueDice")]
[JsonDerivedType(typeof(ObjectEffectCaseValueRideParents), "ValueRideParents")]
[JsonDerivedType(typeof(ObjectEffectCaseValuePaddockGauge), "ValuePaddockGauge")]
[JsonDerivedType(typeof(ObjectEffectCaseValueCreatureFamily), "ValueCreatureFamily")]
[JsonDerivedType(typeof(ObjectEffectCaseValueMonsterCount), "ValueMonsterCount")]
public abstract record ObjectEffectCase;
public sealed record ObjectEffectCaseValueString(string Value) : ObjectEffectCase;
public sealed record ObjectEffectCaseValueDate(ObjectEffectDateValue Value) : ObjectEffectCase;
public sealed record ObjectEffectCaseValueDurationMinutes(long Value) : ObjectEffectCase;
public sealed record ObjectEffectCaseValueInt(int Value) : ObjectEffectCase;
public sealed record ObjectEffectCaseValueMinMax(ObjectEffectMinMaxValue Value) : ObjectEffectCase;
public sealed record ObjectEffectCaseValueDice(ObjectEffectDiceValue Value) : ObjectEffectCase;
public sealed record ObjectEffectCaseValueRideParents(ObjectEffectRideParentsValue Value) : ObjectEffectCase;
public sealed record ObjectEffectCaseValuePaddockGauge(ObjectEffectPaddockGaugeValue Value) : ObjectEffectCase;
public sealed record ObjectEffectCaseValueCreatureFamily(int Value) : ObjectEffectCase;
public sealed record ObjectEffectCaseValueMonsterCount(ObjectEffectMonsterCount Value) : ObjectEffectCase;

public sealed record ObjectEffectDateValue(int Day, int Year, int Month, int Minute, int Hour);

public sealed record ObjectEffectDiceValue(int Const, long Num, int Side);

public sealed record ObjectEffectMinMaxValue(int Max, int Min);

public sealed record ObjectEffectMonsterCount(int CreatureFamily, int Count);

public sealed record ObjectEffectPaddockGaugeValue(int Cap, int GaugeValue);

public sealed record ObjectEffectRideParentsValue(int FirstParent, int SecondParent);

public enum ObjectError { InventoryFull, CannotEquipTwice, NotTradable, CannotDrop, CannotDropNoPlace, CannotDestroy, LevelTooLow, CannotUnequip, CannotEquipHere, Criterions, SymbioticObjectError, EvolutiveObjectRefusedFood }

public sealed record ObjectErrorEvent(ObjectError Reason) : GameEvent;

public sealed record ObjectItem(int Gid, IReadOnlyList<ObjectEffect> Effects, int Quantity, int Uid, IReadOnlyList<ObjectStorageQuantity> StorageQuantities);

public sealed record ObjectItemInventory(int Position, bool Favorite, ObjectItem Item);

public sealed record ObjectMovementEvent(int ObjectUid, int Position) : GameEvent;

public enum ObjectOrigin { OriginUndefined, OriginQuest, Unknown2, Unknown3 }

public sealed record ObjectQuantityEvent(ObjectOrigin Origin, ObjectUidWithQuantity Object) : GameEvent;

public sealed record ObjectSetPositionRequest(int Quantity, int ObjectUid, int Position) : GameRequest;

public enum ObjectStorage { Unknown0, Inventory, Bank, HavenBag }

public sealed record ObjectStorageQuantity(int Quantity, ObjectStorage Storage);

public sealed record ObjectUidWithQuantity(int ObjectUid, int Quantity);

public sealed record OptionalFeaturesEvent(IReadOnlyList<int> Features) : GameEvent;

public sealed record PartyEntity();

public sealed record PartyInvitationMemberInformation(IReadOnlyList<PartyEntity> Entities, MapExtendedCoordinates? Coordinates);

public sealed record PartyMemberInformation(CharacterStatus? Status, IReadOnlyList<PartyEntity> Entities, MapExtendedCoordinates? Coordinates, int? Rank, PartyUpdateCommonsInformation? CommonsInformation, int Initiative);

public sealed record PartyUpdateCommonsInformation();

public sealed record PingRequest(bool Quiet) : GameRequest;

public sealed record PlayerFightFriendlyAnswerRequest(int FightId, bool Accept) : GameRequest;

public sealed record PlayerFightFriendlyAnsweredEvent(int FightId) : GameEvent;

public sealed record PlayerFightFriendlyRequestedEvent(long SourceId, long TargetId, int FightId) : GameEvent;

public sealed record PlayerFightRequest(long TargetId, bool Friendly, int TargetCellId) : GameRequest;

public sealed record PongEvent(bool Quiet) : GameEvent;

public sealed record PortalInformation(int PortalId, int AreaId);

public sealed record PrismCristal(ObjectItemInventory CristalObject, PrismCristalType Type);

public enum PrismCristalType { NoCristal, Protective }

public sealed record PrismInformation(int? CristalNumberLeft, long NextEvolutionDate, PrismState State, long PlacementDate, AllianceInformation? AllianceInformation, PrismCristal? Cristal);

public enum PrismState { Normal, Weakened, Vulnerable, Protected }

public sealed record ProbableGuildMissionStateRefreshRequest() : GameRequest;

public sealed record QueueStatusEvent(int Position, int Total) : GameEvent;

public sealed record RankInformation();

public sealed record ResetCharacterCharacteristicsRequest() : GameRequest;

public enum Restriction { CannotBeAttacked, CannotBeChallenged, CannotTrade, CannotBeAttackedByMutant, CannotRun, ForceSlowWalk, CannotMinimize, CannotMove, CannotAttack, CannotChallenge, CannotExchange, CannotAttackAsMutant, CannotChat, CannotUseObject, CannotUseTaxCollector, CannotUseInteractive, CannotSpeakToNpc, CannotChangeZone, CannotAttackMonster }

public sealed record RolePlayActor(RolePlayActorCase ActorCase);

[JsonDerivedType(typeof(RolePlayActorCaseTreasureHuntNpcId), "TreasureHuntNpcId")]
[JsonDerivedType(typeof(RolePlayActorCasePrismActor), "PrismActor")]
[JsonDerivedType(typeof(RolePlayActorCasePortalActor), "PortalActor")]
[JsonDerivedType(typeof(RolePlayActorCaseMonsterGroupActor), "MonsterGroupActor")]
[JsonDerivedType(typeof(RolePlayActorCaseNamedActor), "NamedActor")]
[JsonDerivedType(typeof(RolePlayActorCaseTaxCollectorActor), "TaxCollectorActor")]
[JsonDerivedType(typeof(RolePlayActorCaseNpcStaticInformation), "NpcStaticInformation")]
public abstract record RolePlayActorCase;
public sealed record RolePlayActorCaseTreasureHuntNpcId(int Value) : RolePlayActorCase;
public sealed record RolePlayActorCasePrismActor(PrismInformation Value) : RolePlayActorCase;
public sealed record RolePlayActorCasePortalActor(PortalInformation Value) : RolePlayActorCase;
public sealed record RolePlayActorCaseMonsterGroupActor(MonsterGroupActor Value) : RolePlayActorCase;
public sealed record RolePlayActorCaseNamedActor(NamedActor Value) : RolePlayActorCase;
public sealed record RolePlayActorCaseTaxCollectorActor(TaxCollectorActor Value) : RolePlayActorCase;
public sealed record RolePlayActorCaseNpcStaticInformation(NpcStaticInformation Value) : RolePlayActorCase;

public sealed record SellingConditions(int NpcContextualId, int MaxItemLevel, int MaxItemPerAccount, int BidId, float TaxPercentage, int UnsoldDelay, float TaxModificationPercentage, IReadOnlyList<int> Types, IReadOnlyList<int> Quantities);

public sealed record SequenceEndEvent(int ActionId, long AuthorId) : GameEvent;

public sealed record SequenceNumberEvent() : GameEvent;

public sealed record SequenceNumberRequest(int Number) : GameRequest;

public sealed record SequenceStartEvent(SequenceType SequenceType) : GameEvent;

public enum SequenceType { Spell, Weapon, GlyphTrap, Triggered, Move, CharacterDeath, TurnStart, TurnEnd, FightStart, Unknown9, Unknown10 }

public sealed record ServerSelectionStatusEvent() : GameEvent;

public sealed record ServerSettingsEvent(int ArenaLeaveBanTime, ServerType GameType, bool HasFreeAutopilot, bool IsMonoAccount, int Community, string Language) : GameEvent;

public enum ServerType { Undefined, Classical, Kolizeum, Tournament, Epic, Temporis }

public sealed record Shortcut(int SlotId, ShortcutCase ShortcutCase);

public enum ShortcutBar { General, Spell }

public sealed record ShortcutBarAddRequest(Shortcut Shortcut, ShortcutBar BarType) : GameRequest;

public sealed record ShortcutBarContentEvent(IReadOnlyList<Shortcut> Shortcuts, ShortcutBar BarType) : GameEvent;

public sealed record ShortcutBarRefreshEvent(Shortcut Shortcut, ShortcutBar BarType) : GameEvent;

public sealed record ShortcutBarRemoveRequest(ShortcutBar BarType, int SlotId) : GameRequest;

public sealed record ShortcutBarRemovedEvent(ShortcutBar BarType, int SlotId) : GameEvent;

public sealed record ShortcutBarReplacedEvent(Shortcut Shortcut, ShortcutBar BarType) : GameEvent;

public sealed record ShortcutBarSwapRequest(int FirstSlotId, int SecondSlotId, ShortcutBar BarType) : GameRequest;

[JsonDerivedType(typeof(ShortcutCaseShortcutObjectItem), "ShortcutObjectItem")]
[JsonDerivedType(typeof(ShortcutCaseShortcutPreset), "ShortcutPreset")]
[JsonDerivedType(typeof(ShortcutCaseShortcutSpell), "ShortcutSpell")]
[JsonDerivedType(typeof(ShortcutCaseShortcutSmiley), "ShortcutSmiley")]
[JsonDerivedType(typeof(ShortcutCaseShortcutEmote), "ShortcutEmote")]
[JsonDerivedType(typeof(ShortcutCaseShortcutCosmeticObject), "ShortcutCosmeticObject")]
[JsonDerivedType(typeof(ShortcutCaseShortcutOutfit), "ShortcutOutfit")]
public abstract record ShortcutCase;
public sealed record ShortcutCaseShortcutObjectItem(ShortcutObjectItem Value) : ShortcutCase;
public sealed record ShortcutCaseShortcutPreset(ShortcutPreset Value) : ShortcutCase;
public sealed record ShortcutCaseShortcutSpell(ShortcutSpell Value) : ShortcutCase;
public sealed record ShortcutCaseShortcutSmiley(ShortcutSmiley Value) : ShortcutCase;
public sealed record ShortcutCaseShortcutEmote(ShortcutEmote Value) : ShortcutCase;
public sealed record ShortcutCaseShortcutCosmeticObject(ShortcutCosmeticObject Value) : ShortcutCase;
public sealed record ShortcutCaseShortcutOutfit(ShortcutOutfit Value) : ShortcutCase;

public sealed record ShortcutCosmeticObject();

public sealed record ShortcutEmote();

public sealed record ShortcutObjectItem(int ItemGid, int ItemUid);

public sealed record ShortcutOutfit();

public sealed record ShortcutPreset();

public sealed record ShortcutSmiley();

public sealed record ShortcutSpell(int SpellId);

public sealed record SocialMember(long HoursSinceLastConnection, int RankId, CharacterState State, long AccountId, int BreedId, GuildMember? GuildMember, CharacterStatus? Status, Gender Gender, long EnrollmentDate);

public sealed record SpawnInformation(Team Team, bool Alive, ActorPositionInformation? Position);

public sealed record SpellCastCount(IReadOnlyList<SpellCastPerTargetCount> PerTargetCounts, int Count, long EntityId);

public sealed record SpellCastPerTargetCount(int Count, long TargetId);

public sealed record SpellGlobalCastCount(IReadOnlyList<SpellCastPerTargetCount> PerTargetCounts, int Count);

public sealed record SpellItem(int SpellLevel, int SpellId, SpellType SpellType);

public sealed record SpellModifier(int Equipment, SpellModifierActionType ActionType, int Context, SpellModifierType ModifierType, int SpellId);

public enum SpellModifierActionType { ActionInvalid, ActionBoost, ActionRemoveBoost, ActionSet }

public enum SpellModifierType { InvalidModification, Range, Damage, BaseDamage, HealBonus, ApCost, CastInterval, CriticalHitBonus, CastLine, Los, MaxCastPerTurn, MaxCastPerTarget, RangeMax, RangeMin, OccupiedCell, FreeCell, VisibleTarget, PortalFreeCell, PortalProjection, Unknown19, Unknown20 }

public enum SpellType { Unknown0, Unknown1, Unknown2, Unknown3, Unknown4, Unknown5, Unknown6, Unknown7, Unknown8, Unknown9 }

public sealed record SpellsEvent(IReadOnlyList<SpellItem> HumanSpells, IReadOnlyList<SpellItem> MutantSpells) : GameEvent;

public sealed record TaxCollectorActor(TaxCollectorStaticInformation Identification, int Attack);

public sealed record TaxCollectorStaticInformation(AllianceInformation? Alliance, int LastNameId, string Uid, long CallerId, int FirstNameId);

public enum Team { Challenger, Defender, Spectator, Unknown3 }

public enum TeamType { Player, Monster, Mutant, TaxCollector, BadPlayer, Prism }

public sealed record TextInformationEvent(TextInformationType MessageType, int MessageId, IReadOnlyList<string> Parameters) : GameEvent;

public enum TextInformationType { TextInformationMessage, TextInformationError, TextInformationPvp, TextInformationFightLog, TextInformationPopup, TextLivingObject, TextEntityTalk, TextInformationFight, TextInformationEvent, TextInformationWorldEventUpcoming, TextInformationWorldEventStarted, TextInformationWorldEventEnded, Unknown13 }

public sealed record TimeEvent(int TimezoneOffset, long Timestamp) : GameEvent;

public sealed record TokenValidationRequest(string Token) : GameRequest;

public sealed record TrustStatusEvent() : GameEvent;

public sealed record UnknownJqj();

public sealed record UnknownJrn();

public sealed record UpdateLifePointsEvent() : GameEvent;
