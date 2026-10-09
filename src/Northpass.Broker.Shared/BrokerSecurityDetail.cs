namespace Northpass.Broker;

// Fixed vocabulary only: never serialize SIDs, LUIDs, paths, token handles or
// exception messages. Evidence is advisory and cannot authorize a peer.
public enum BrokerSecurityCheck
{
    None, ProcessOpen, ProcessAlive, CreationTimeQuery, CreationTime,
    ImageQuery, ImagePath, PeerTokenOpen, CurrentTokenOpen, PeerUserQuery,
    CurrentUserQuery, UserSid, PeerStatisticsQuery, CurrentStatisticsQuery,
    PeerSessionQuery, CurrentSessionQuery, SessionId, AuthenticationId,
    ElevationQuery, Elevated, GroupsQuery, AdministratorGroup,
    WorkerCreationOrder, SnapshotOpen, SnapshotRead, WorkerParent,
    PeerElevationQuery, CurrentElevationQuery, CurrentLinkedTokenQuery, PeerLinkedTokenQuery,
    LinkedIdentity, LinkedDirection, TokenTypeQuery, TokenChanged
}
public enum BrokerSecurityOutcome { None, ApiFailure, IdentityMismatch, InvalidData, ProcessExited, Authorized }
public enum BrokerLinkedTokenStatus { NotChecked, NotSplit, MatchesPeer, DifferentIdentity, QueryFailed }
public sealed record BrokerSecurityDetail(BrokerSecurityCheck Check, BrokerSecurityOutcome Outcome,
    int Code, BrokerLinkedTokenStatus Linked=BrokerLinkedTokenStatus.NotChecked, int LinkedCode=0)
{
    public string Diagnostic => $"check={Check} outcome={Outcome} code={Code} linked={Linked} linked_code={LinkedCode}";
}
public sealed class BrokerSecurityException(BrokerSecurityDetail detail) : UnauthorizedAccessException(detail.Diagnostic)
{
    public BrokerSecurityDetail Detail { get; } = detail;
}

public enum BrokerAuthorizationMode { SameLogon, UacLinkedLogon }
internal sealed record BrokerTokenIdentity(string UserSid,ulong AuthenticationId,int SessionId);
internal sealed record BrokerTokenSnapshot(BrokerTokenIdentity Identity,ulong TokenId,int TokenType,
    int ElevationType,bool Elevated,bool AdministratorEnabled);
internal sealed record BrokerTokenLink(BrokerTokenSnapshot Peer,BrokerTokenSnapshot Current,
    BrokerTokenSnapshot CurrentLinked,BrokerTokenSnapshot PeerLinked);
internal static class BrokerIdentityPolicy
{
    internal static BrokerAuthorizationMode Validate(BrokerTokenIdentity peer,BrokerTokenIdentity current,
        bool requireAdmin,Func<BrokerTokenLink> readLiveLink)
    {
        if(peer.UserSid!=current.UserSid)throw Reject(BrokerSecurityCheck.UserSid);
        if(peer.SessionId!=current.SessionId)throw Reject(BrokerSecurityCheck.SessionId);
        if(peer.AuthenticationId==current.AuthenticationId)return BrokerAuthorizationMode.SameLogon;
        // This callback is an internal live Windows reader, not a diagnostic
        // status, persisted record, IPC message or cached authorization decision.
        var link=readLiveLink();
        if(link.Peer.Identity!=peer || link.Current.Identity!=current)throw Reject(BrokerSecurityCheck.TokenChanged);
        int peerType=requireAdmin?2:3,currentType=requireAdmin?3:2;
        if(!Direction(link.Peer,peerType) || !Direction(link.Current,currentType) ||
            !Direction(link.CurrentLinked,peerType) || !Direction(link.PeerLinked,currentType))
            throw Reject(BrokerSecurityCheck.LinkedDirection);
        // Process-primary tokens can be duplicated by Windows on child creation.
        // TokenId identifies the held object, not its linked logon identity.
        // Bind both OS links to the exact observed peer/current logon identities.
        if(link.CurrentLinked.Identity!=peer || link.PeerLinked.Identity!=current)
            throw Reject(BrokerSecurityCheck.LinkedIdentity);
        return BrokerAuthorizationMode.UacLinkedLogon;
    }
    private static bool Direction(BrokerTokenSnapshot token,int type)=>
        token.TokenType is 1 or 2 && token.ElevationType==type && token.Elevated==(type==2) && token.AdministratorEnabled==(type==2);
    private static BrokerSecurityException Reject(BrokerSecurityCheck check)=>
        new(new(check,BrokerSecurityOutcome.IdentityMismatch,unchecked((int)0x80070005)));
}
