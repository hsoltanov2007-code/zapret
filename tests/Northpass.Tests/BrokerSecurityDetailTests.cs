using Northpass.Broker;
namespace Northpass.Tests;

public sealed class BrokerSecurityDetailTests
{
    private static BrokerTokenSnapshot Token(ulong logon,int type,string user="user",int session=1,ulong tokenId=1)=>
        new(new(user,logon,session),tokenId,1,type,type==2,type==2);
    private static BrokerTokenLink Valid(bool worker)=>worker
        ?new(Token(20,2),Token(10,3),Token(20,2,tokenId:99),Token(10,3,tokenId:98))
        :new(Token(10,3),Token(20,2),Token(10,3,tokenId:99),Token(20,2,tokenId:98));
    [Theory][InlineData(false)][InlineData(true)]
    public void SimulatedValidUacPairAcceptsBothDirectionsWithPrimaryTokenClones(bool worker)
    {
        var link=Valid(worker);int reads=0;
        Assert.Equal(BrokerAuthorizationMode.UacLinkedLogon,BrokerIdentityPolicy.Validate(link.Peer.Identity,link.Current.Identity,worker,()=>{reads++;return link;}));
        Assert.Equal(1,reads); // no cached diagnostic or previous proof used
    }
    [Theory][InlineData("other",1,BrokerSecurityCheck.UserSid)][InlineData("user",2,BrokerSecurityCheck.SessionId)]
    public void DifferentUserOrSessionCannotReachLinkedAuthorization(string user,int session,BrokerSecurityCheck check)
    {
        var error=Assert.Throws<BrokerSecurityException>(()=>BrokerIdentityPolicy.Validate(new(user,10,session),new("user",20,1),false,()=>throw new Exception("Must not inspect links")));
        Assert.Equal(check,error.Detail.Check);
    }
    [Fact] public void IdenticalIdentityDoesNotQueryLinkedToken()=>Assert.Equal(BrokerAuthorizationMode.SameLogon,
        BrokerIdentityPolicy.Validate(new("user",10,1),new("user",10,1),false,()=>throw new Exception("Unexpected query")));
    [Theory][InlineData(false)][InlineData(true)]
    public void UnrelatedLogonRejectedDespiteSameUserAndSession(bool worker)
    {
        var link=Valid(worker);
        link=link with {CurrentLinked=link.CurrentLinked with {Identity=link.CurrentLinked.Identity with {AuthenticationId=999}}};
        Assert.Equal(BrokerSecurityCheck.LinkedIdentity,Assert.Throws<BrokerSecurityException>(()=>BrokerIdentityPolicy.Validate(link.Peer.Identity,link.Current.Identity,worker,()=>link)).Detail.Check);
    }
    [Fact] public void OneSidedLinkCannotAuthorize()
    {
        var link=Valid(false);link=link with {PeerLinked=link.PeerLinked with {Identity=new("user",999,1)}};
        Assert.Equal(BrokerSecurityCheck.LinkedIdentity,Assert.Throws<BrokerSecurityException>(()=>BrokerIdentityPolicy.Validate(link.Peer.Identity,link.Current.Identity,false,()=>link)).Detail.Check);
    }
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)]
    public void IncorrectElevationDirectionCannotAuthorize(int peerType)
    {
        var link=Valid(false);link=link with {Peer=Token(10,peerType)};
        Assert.Equal(BrokerSecurityCheck.LinkedDirection,Assert.Throws<BrokerSecurityException>(()=>BrokerIdentityPolicy.Validate(link.Peer.Identity,link.Current.Identity,true,()=>link)).Detail.Check);
    }
    [Theory][InlineData("sid")][InlineData("session")][InlineData("elevation")][InlineData("admin")][InlineData("token-type")]
    public void IncorrectLinkedIdentityOrCapabilitiesFailClosed(string mismatch)
    {
        var link=Valid(true);var linked=link.CurrentLinked;
        linked=mismatch switch
        {
            "sid"=>linked with {Identity=linked.Identity with {UserSid="other"}},
            "session"=>linked with {Identity=linked.Identity with {SessionId=2}},
            "elevation"=>linked with {Elevated=false},
            "admin"=>linked with {AdministratorEnabled=false},
            _=>linked with {TokenType=99}
        };
        link=link with {CurrentLinked=linked};
        Assert.Throws<BrokerSecurityException>(()=>BrokerIdentityPolicy.Validate(link.Peer.Identity,link.Current.Identity,true,()=>link));
    }
    [Fact] public void ChangedHeldIdentityCannotAuthorize()
    {
        var link=Valid(false);var initial=link.Peer.Identity;
        link=link with {Peer=link.Peer with {Identity=initial with {AuthenticationId=12}}};
        Assert.Equal(BrokerSecurityCheck.TokenChanged,Assert.Throws<BrokerSecurityException>(()=>BrokerIdentityPolicy.Validate(initial,link.Current.Identity,false,()=>link)).Detail.Check);
    }
    [Fact] public void LinkedQueryFailureIsPreservedAndNoAuthorizationIsReturned()
    {
        var expected=new BrokerSecurityException(new(BrokerSecurityCheck.CurrentLinkedTokenQuery,BrokerSecurityOutcome.ApiFailure,5));
        var link=Valid(false);
        Assert.Same(expected,Assert.Throws<BrokerSecurityException>(()=>BrokerIdentityPolicy.Validate(link.Peer.Identity,link.Current.Identity,false,()=>throw expected)));
    }
    [Theory]
    [InlineData("NPB2 6 250 5 123 8 1 0 0",BrokerSecurityOutcome.ApiFailure)]
    [InlineData("NPB2 6 250 -2147024891 123 17 2 2 0",BrokerSecurityOutcome.IdentityMismatch)]
    public void ExactFailureSurvivesTheEvidenceProtocol(string wire,BrokerSecurityOutcome outcome)
    {
        var evidence=BrokerEvidenceRecord.Parse(wire);
        Assert.Equal(outcome,evidence.Security!.Outcome);
        var failure=new BrokerFailure(BrokerFailureKind.AuthenticationRejected,evidence.Stage,2365,evidence.Code,0x4e500602,true,evidence.Security);
        Assert.Contains("check=",failure.Diagnostic);
        Assert.Contains("outcome="+outcome,failure.Diagnostic);
        Assert.Equal(evidence.Code,BrokerStartup.SafeCode(new BrokerSecurityException(evidence.Security)));
    }
    [Theory]
    [InlineData("NPB2 6 1 5 1 0 1 0 0")]
    [InlineData("NPB2 6 1 5 1 999 1 0 0")]
    [InlineData("NPB2 6 1 5 1 8 0 0 0")]
    [InlineData("NPB2 6 1 5 1 8 99 0 0")]
    [InlineData("NPB2 6 1 5 1 8 1 99 0")]
    [InlineData("NPB2 6 1 5 1 8 1 0 secret")]
    [InlineData("NPB2 6 1 5 1 8 1 0 0 extra")]
    public void DiagnosticVocabularyIsClosed(string wire)=>Assert.Throws<InvalidDataException>(()=>BrokerEvidenceRecord.Parse(wire));
}
