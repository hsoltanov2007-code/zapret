using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Northpass.Broker;
namespace Northpass.Windows.Tests;

public sealed class BrokerSecurityWindowsTests
{
    [Fact] public void ActualProcessIdentityAndQueryOnlyTokenAreAccepted()
    {
        using var process=Process.GetCurrentProcess();
        BrokerSecurity.ValidatePeer(process,Environment.ProcessPath!,BrokerSecurity.CreationTime(process),BrokerSecurity.IsAdministrator);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void ActualProcessCreationAndImageMismatchAreSpecific(bool creation)
    {
        using var process=Process.GetCurrentProcess();
        var failure=Assert.Throws<BrokerSecurityException>(()=>BrokerSecurity.ValidatePeer(process,
            creation?Environment.ProcessPath!:Path.Combine(Path.GetTempPath(),"wrong-owner.exe"),
            BrokerSecurity.CreationTime(process)+(creation?1:0),false));
        Assert.Equal(creation?BrokerSecurityCheck.CreationTime:BrokerSecurityCheck.ImagePath,failure.Detail.Check);
        Assert.Equal(BrokerSecurityOutcome.IdentityMismatch,failure.Detail.Outcome);
        // A failed validation does not poison the next ownership check.
        BrokerSecurity.ValidatePeer(process,Environment.ProcessPath!,BrokerSecurity.CreationTime(process),false);
    }
    [Fact] public void RealTokenQueryAccessDeniedIsNotAnIdentityMismatch()
    {
        using var identity=WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        using var process=Process.GetCurrentProcess();
        Assert.True(DuplicateHandle(process.Handle,identity.AccessToken,process.Handle,out var noQuery,0,false,0));
        using(noQuery)
        {
            var failure=Assert.Throws<BrokerSecurityException>(()=>BrokerSecurity.ValidateTokenIdentity(noQuery,identity.AccessToken,false));
            Assert.Equal(BrokerSecurityCheck.PeerUserQuery,failure.Detail.Check);
            Assert.Equal(BrokerSecurityOutcome.ApiFailure,failure.Detail.Outcome);
            Assert.Equal(5,failure.Detail.Code);
        }
        BrokerSecurity.ValidateTokenIdentity(identity.AccessToken,identity.AccessToken,false);
    }
    [Fact] public void RealSplitTokenRelationshipIsRecordedWhenAvailable()
    {
        using var identity=WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        byte[] type=new byte[4];Assert.True(GetTokenInformation(identity.AccessToken,18,type,4,out int returned));Assert.Equal(4,returned);
        string result;
        if(BitConverter.ToInt32(type)==1)
            result="NOT EXERCISED: runner has TokenElevationTypeDefault; no real UAC linked-token pair. Interactive acceptance required.";
        else
        {
            byte[] bytes=new byte[IntPtr.Size];Assert.True(GetTokenInformation(identity.AccessToken,19,bytes,bytes.Length,out returned));Assert.Equal(bytes.Length,returned);
            using var linked=new SafeAccessTokenHandle(new IntPtr(BitConverter.ToInt64(bytes)));
            // Linked tokens may have different authentication IDs. This release
            // records a verified relationship but deliberately still rejects it.
            try {BrokerSecurity.ValidateTokenIdentity(linked,identity.AccessToken,false);result="Actual linked token pair: existing same-logon policy accepted.";}
            catch(BrokerSecurityException failure)
            {
                Assert.Equal(BrokerSecurityCheck.AuthenticationId,failure.Detail.Check);
                Assert.Equal(BrokerLinkedTokenStatus.MatchesPeer,failure.Detail.Linked);
                result="Actual linked token pair: equality policy rejected; Windows linked-token relationship verified. "+failure.Detail.Diagnostic;
            }
        }
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../TestResults"));Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"engine-broker-token-coverage.txt"),result);
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DuplicateHandle(IntPtr source,SafeAccessTokenHandle token,IntPtr target,out SafeAccessTokenHandle duplicate,uint access,bool inherit,uint options);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,byte[] data,int size,out int returned);
}
