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
    WorkerCreationOrder, SnapshotOpen, SnapshotRead, WorkerParent
}
public enum BrokerSecurityOutcome { None, ApiFailure, IdentityMismatch, InvalidData, ProcessExited }
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

internal sealed record BrokerTokenIdentity(string UserSid,ulong AuthenticationId,int SessionId);
internal static class BrokerIdentityPolicy
{
    internal static void Validate(BrokerTokenIdentity peer,BrokerTokenIdentity current,
        Func<(BrokerLinkedTokenStatus Status,int Code)> inspectLinked)
    {
        BrokerSecurityCheck check=peer.UserSid!=current.UserSid?BrokerSecurityCheck.UserSid:
            peer.SessionId!=current.SessionId?BrokerSecurityCheck.SessionId:
            peer.AuthenticationId!=current.AuthenticationId?BrokerSecurityCheck.AuthenticationId:BrokerSecurityCheck.None;
        if(check==BrokerSecurityCheck.None)return;
        var linked=check==BrokerSecurityCheck.AuthenticationId?inspectLinked():(BrokerLinkedTokenStatus.NotChecked,0);
        throw new BrokerSecurityException(new(check,BrokerSecurityOutcome.IdentityMismatch,unchecked((int)0x80070005),linked.Item1,linked.Item2));
    }
}
