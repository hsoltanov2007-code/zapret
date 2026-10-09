using Northpass.Broker;
namespace Northpass.Tests;

public sealed class BrokerSecurityDetailTests
{
    [Theory]
    [InlineData("other",10UL,1,BrokerSecurityCheck.UserSid)]
    [InlineData("user",11UL,1,BrokerSecurityCheck.AuthenticationId)]
    [InlineData("user",10UL,2,BrokerSecurityCheck.SessionId)]
    public void DifferentIdentityFailsClosed(string user,ulong logon,int session,BrokerSecurityCheck check)
    {
        var error=Assert.Throws<BrokerSecurityException>(()=>BrokerIdentityPolicy.Validate(new(user,logon,session),new("user",10,1),()=> (BrokerLinkedTokenStatus.MatchesPeer,0)));
        Assert.Equal(check,error.Detail.Check);
        Assert.Equal(BrokerSecurityOutcome.IdentityMismatch,error.Detail.Outcome);
        Assert.Equal(check==BrokerSecurityCheck.AuthenticationId?BrokerLinkedTokenStatus.MatchesPeer:BrokerLinkedTokenStatus.NotChecked,error.Detail.Linked);
    }
    [Fact] public void IdenticalIdentityDoesNotQueryLinkedToken()=>BrokerIdentityPolicy.Validate(new("user",10,1),new("user",10,1),()=>throw new Exception("Unexpected query"));
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
