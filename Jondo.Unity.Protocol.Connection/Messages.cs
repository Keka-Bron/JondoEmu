namespace Jondo.Unity.Protocol.Connection;

// Hand-written semantic types. Protobuf tags, aliases, storage and decoy fields belong
// to the wire project; database entities belong to the server.
public abstract record ConnectionMessage;
public sealed record ConnectionRequest(string CorrelationId, RequestContent Content) : ConnectionMessage;
public sealed record ConnectionResponse(string CorrelationId, ResponseContent Content) : ConnectionMessage;
public sealed record ConnectionEvent(EventContent Content) : ConnectionMessage;

public abstract record RequestContent;
public sealed record Ping : RequestContent;
public sealed record IdentificationRequest(string DeviceIdentifier, IdentificationMethod Method) : RequestContent;
public sealed record SelectServerRequest(int ServerId) : RequestContent;
public sealed record ForceAccountRequest(long AccountId) : RequestContent;
public sealed record ReleaseAccountRequest : RequestContent;
public sealed record FriendListRequest : RequestContent;
public sealed record AcquaintanceServersRequest(string Name, string Tag) : RequestContent;

public abstract record IdentificationMethod;
public sealed record TokenIdentification(string Token, ShieldCertificate? Shield) : IdentificationMethod;
public sealed record LoginIdentification(string Login) : IdentificationMethod;
public sealed record ShieldCertificate(long CertificateId, string CertificateHash);

public abstract record ResponseContent;
public sealed record Pong : ResponseContent;
public sealed record IdentificationResponse(IdentificationResult Result) : ResponseContent;
public sealed record SelectServerResponse(SelectServerResult Result) : ResponseContent;
public sealed record ForceAccountResponse(ForceAccountResult Result) : ResponseContent;
public sealed record FriendListResponse(FriendListResult Result) : ResponseContent;
public sealed record AcquaintanceServersResponse(AcquaintanceServersResult Result) : ResponseContent;

public abstract record IdentificationResult;
public sealed record IdentificationSuccess(
    long AccountId,
    string Nickname,
    string Tag,
    ServerList? ServerList,
    string SubscriptionEndDate,
    AccountRights? Rights,
    int? FightReconnectionServerId,
    ForceAccountStatus? ForceAccount,
    MaintenanceWindow? Maintenance) : IdentificationResult;
public sealed record IdentificationError(IdentificationErrorReason Reason, string BanEndDate) : IdentificationResult;
public sealed record AccountRights(bool ShowForceAccount, bool ShowConsole, bool UnlimitedAccess, bool InfiniteSubscription);

public abstract record SelectServerResult;
public sealed record SelectServerSuccess(string Ticket, string Host, IReadOnlyList<int> Ports) : SelectServerResult;
public sealed record SelectServerError(SelectServerErrorReason Reason) : SelectServerResult;

public abstract record ForceAccountResult;
public sealed record ForceAccountSuccess(ForceAccountStatus Status) : ForceAccountResult;
public sealed record ForceAccountError : ForceAccountResult;
public sealed record ForceAccountStatus(bool IsForced, long AccountId, string Nickname, string Tag, ServerList? ServerList);

public abstract record FriendListResult;
public sealed record FriendListSuccess(IReadOnlyList<ConnectionFriend> Friends) : FriendListResult;
public sealed record FriendListError(FriendListErrorReason Reason) : FriendListResult;
public sealed record ConnectionFriend(string Name, string Tag, IReadOnlyList<int> Servers);

public abstract record AcquaintanceServersResult;
public sealed record AcquaintanceServersSuccess(IReadOnlyList<int> Servers) : AcquaintanceServersResult;
public sealed record AcquaintanceServersError(AcquaintanceErrorReason Reason) : AcquaintanceServersResult;

public abstract record EventContent;
public sealed record UpdateServerEvent(ServerInformation? ServerInformation) : EventContent;

public sealed record ServerList(IReadOnlyList<ServerInformation> Servers, IReadOnlyList<ServerSlots> Slots);
public sealed record ServerSlots(ServerCategory Category, int MaxSlots);
public sealed record ServerInformation(Server? Server, ServerAccessibility Accessibility, IReadOnlyList<CharacterSummary> Characters);
public sealed record Server(int Id, ConnectionServerStatus Status, ServerCategory Category);
public sealed record CharacterSummary(string Name, CharacterBreed Breed, CharacterGender Gender, int Level, string LastConnectionDate);
public sealed record MaintenanceWindow(string Start, string End, IReadOnlyList<int> ServerIds);

// The order and numeric values of these enums are NOT a protobuf contract. The
// semantic mapping explicitly binds every supported value in each direction.
public enum ServerCategory { MonoAccount, Classic, PioneerMonoAccount, PioneerClassic, Tournament, Epic, Temporis }
public enum ConnectionServerStatus { Offline, Online }
public enum ServerAccessibility { Accessible, SubscribeRestriction, MonoAccountRestriction, Unknown3 }
public enum CharacterGender { Male, Female }
public enum CharacterBreed
{
    Feca, Osamodas, Enutrof, Sram, Xelor, Ecaflip, Eniripsa, Iop, Cra, Sadida,
    Sacrier, Pandawa, Rogue, Masqueraider, Foggernaut, Eliotrope, Huppermage, Ouginak, Forgelance
}
public enum IdentificationErrorReason
{
    UnknownAuthError, AlreadyConnected, OtpTimeout, Banned, InvalidShieldCertificate,
    Locked, CredentialsReset, WrongCredentials, EmailUnvalidated, AnonymousIpForbidden,
    NicknameRegistration, Unauthorized, Unknown12, Unknown13, Unknown14
}
public enum SelectServerErrorReason { Refused, SubscriberOnly, SingleAccountVerifiedOnly, Maintenance }
public enum FriendListErrorReason { Unknown, Flooding }
public enum AcquaintanceErrorReason { Unknown, NoResult, Flood, InvalidAccount }
