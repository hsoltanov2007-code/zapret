using System.ComponentModel;
using Northpass.Broker;
using Northpass.Services;

namespace Northpass.Tests;
public sealed class BrokerStartupTests
{
    private static Task Pending()=>new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    [Fact] public async Task SuccessfulOperationDoesNotWaitForTheDeadline()=>await BrokerStartup.AwaitAsync(Task.CompletedTask,Pending(),default,TimeSpan.FromSeconds(15));
    [Fact] public async Task EarlyOwnedExitIsNotReportedAsTimeout()
    {
        await Assert.ThrowsAsync<BrokerHelperExitedException>(()=>BrokerStartup.AwaitAsync(Pending(),Task.CompletedTask,default,TimeSpan.FromSeconds(15)));
        Assert.Equal(BrokerStage.WorkerOwner,BrokerStartup.ExitStage(0x4e500601));
        Assert.Equal(BrokerStage.BootstrapIntegrity,BrokerStartup.ExitStage(0x4e510401));
        Assert.Null(BrokerStartup.ExitStage(17));Assert.Null(BrokerStartup.ExitStage(0x4e50ff01));
    }
    [Fact] public async Task ExitWinsWhenPipeAndExitCompleteTogether()=>await Assert.ThrowsAsync<BrokerHelperExitedException>(()=>BrokerStartup.AwaitAsync(Task.CompletedTask,Task.CompletedTask,default,TimeSpan.Zero));
    [Fact] public async Task ExternalCancellationIsDistinct()
    {
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>BrokerStartup.AwaitAsync(Pending(),Pending(),cancelled.Token,TimeSpan.FromSeconds(15)));
        Assert.Equal(BrokerFailureKind.Cancelled,BrokerStartup.Classify(new OperationCanceledException(),true,BrokerStage.PipeConnect,null));
        Assert.Equal(BrokerFailureKind.StartupTimeout,BrokerStartup.Classify(new OperationCanceledException(),false,BrokerStage.PipeConnect,null));
    }
    [Fact] public async Task DeadlineRemainsBounded()=>await Assert.ThrowsAsync<TimeoutException>(()=>BrokerStartup.AwaitAsync(Pending(),Pending(),default,TimeSpan.FromMilliseconds(20)));
    [Fact] public async Task ExpiredBudgetDoesNotBecomeAnArgumentError()=>await Assert.ThrowsAsync<TimeoutException>(()=>BrokerStartup.AwaitAsync(Pending(),Pending(),default,TimeSpan.FromMilliseconds(-50)));
    [Fact] public async Task CleanupTimeoutReportsPendingAndCanBeRetried()
    {
        bool exited=false;
        Assert.False(await BrokerStartup.WaitForCleanupAsync(()=>exited,TimeSpan.FromMilliseconds(20)));
        exited=true;Assert.True(await BrokerStartup.WaitForCleanupAsync(()=>exited,TimeSpan.FromMilliseconds(20)));
        // Timeout must not be represented as a completed release.
        var failure=new BrokerFailure(BrokerFailureKind.CleanupIncomplete,BrokerStage.Cleanup,20,0,null,false);
        Assert.Contains("cleanup=pending",failure.Diagnostic);
    }
    [Fact] public async Task AuthenticationFailureIsPreserved()
    {
        var original=new UnauthorizedAccessException();
        var observed=await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>BrokerStartup.AwaitAsync(Task.FromException(original),Pending(),default,TimeSpan.FromSeconds(15)));
        Assert.Same(original,observed);Assert.Equal(BrokerFailureKind.AuthenticationRejected,BrokerStartup.Classify(observed,false,BrokerStage.Authentication,null));
    }
    [Theory][InlineData(1223,BrokerFailureKind.UacDenied)][InlineData(5,BrokerFailureKind.LaunchFailed)]
    public void UacDenialIsOnlyTheShellStage(int code,BrokerFailureKind expected)
    {
        Assert.Equal(expected,BrokerStartup.Classify(new Win32Exception(code),false,BrokerStage.Elevation,null));
        Assert.NotEqual(BrokerFailureKind.UacDenied,BrokerStartup.Classify(new Win32Exception(1223),false,BrokerStage.PipeConnect,null));
    }
    [Fact] public void ProtectedInstallationFailureIsNotUacDenial()=>Assert.Equal(BrokerFailureKind.InstallationRejected,BrokerStartup.Classify(new UnauthorizedAccessException(),false,BrokerStage.Installation,null));
    [Theory]
    [InlineData("NPB1 6 29 -2147024891 123")]
    [InlineData("NPB1 4 0 13 0")]
    public void NumericEvidenceDoesNotContainTokensOrPayloads(string line)
    {var evidence=BrokerEvidenceRecord.Parse(line);Assert.Contains("stage=",evidence.Diagnostic);Assert.DoesNotContain("NPB1",evidence.Diagnostic);}
    [Theory]
    [InlineData("NPB1 99 1 0 2")][InlineData("NPB1 6 -1 0 2")][InlineData("NPB1 6 3600001 0 2")]
    [InlineData("NPB1 6 1 0 -2")][InlineData("NPB1 6 1 0 2 secret")][InlineData("NPB1 6 1 0 2\nAUTH")]
    public void MalformedEvidenceFailsClosed(string line)=>Assert.Throws<InvalidDataException>(()=>BrokerEvidenceRecord.Parse(line));
    [Fact] public void UnchangedErrorsAreBoundedAndEachAttemptCanReportAgain()
    {
        var errors=new ConnectionErrorTracker();Assert.True(errors.Observe("failed"));
        for(int poll=0;poll<10000;poll++)Assert.False(errors.Observe("failed"));
        Assert.True(errors.Observe("new failure"));errors.BeginAttempt();Assert.True(errors.Observe("new failure"));
        Assert.False(errors.Observe(null));Assert.True(errors.Observe("new failure"));
    }
    [Fact] public void LocalEvidenceIsAtomicAndBoundedAcrossRetries()
    {
        string folder=Path.Combine(Path.GetTempPath(),"northpass-journal-"+Guid.NewGuid().ToString("N"));
        try
        {
            var failure=new BrokerFailure(BrokerFailureKind.HelperExited,BrokerStage.WorkerOwner,31,5,17,true);
            BrokerFailureJournal.Save(folder,failure,[new(BrokerStage.WorkerOwner,15,5,123)]);
            using(var saved=System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder,"broker-startup.json"))))
            {Assert.Equal(17,saved.RootElement.GetProperty("Failure").GetProperty("HelperExitCode").GetInt32());Assert.Single(saved.RootElement.GetProperty("Stages").EnumerateArray());}
            for(int retry=0;retry<100;retry++)BrokerFailureJournal.Save(folder,failure,[]);
            Assert.Single(Directory.GetFiles(folder));Assert.True(new FileInfo(Path.Combine(folder,"broker-startup.json")).Length<16384);
            Assert.Throws<InvalidDataException>(()=>BrokerFailureJournal.Save(folder,failure,Enumerable.Repeat(new BrokerEvidenceRecord(BrokerStage.WorkerOwner,1,0,1),65).ToArray()));
        }
        finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    [Theory][InlineData(BrokerFailureKind.UacDenied,"PermissionDeclined")][InlineData(BrokerFailureKind.StartupTimeout,"BrokerTimeout")]
    [InlineData(BrokerFailureKind.HelperExited,"BrokerExited")][InlineData(BrokerFailureKind.AuthenticationRejected,"BrokerRejected")]
    [InlineData(BrokerFailureKind.CleanupIncomplete,"BrokerCleanup")][InlineData(BrokerFailureKind.EngineLaunchFailed,"ConnectionFailed")]
    public void PollingRetainsTheLocalizedErrorKind(BrokerFailureKind kind,string key)
    {
        var failure=new BrokerFailure(kind,BrokerStage.PipeConnect,149,5,17,false);
        Assert.Equal(key,BrokerFailureMessage.Key(new BrokerStartupException(failure)));
        Assert.Equal(key,BrokerFailureMessage.Key(new IOException(failure.Diagnostic)));
        Assert.Contains("helper_exit=17 cleanup=pending",failure.Diagnostic);
    }
}
