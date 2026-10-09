using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using Northpass.Broker;

namespace Northpass.Windows.Tests;

public sealed class BrokerEvidenceWindowsTests
{
    [Fact]
    public async Task EvidenceCleanupDoesNotRequireTheBlockedWpfDispatcher()
    {
        // Actual Windows pipes and a deliberately blocked STA context. This is
        // an IPC lifecycle regression, not privileged authorization or UAC.
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                string id=BrokerPipe.NewId();
                var evidence=new BrokerStartupEvidence(id,_=>{});
                using var owner=Process.GetCurrentProcess();evidence.Own(owner);
                using var client=Task.Run(()=>BrokerPipe.ConnectEvidenceAsync(id,false,default)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                client.Write(Encoding.ASCII.GetBytes("NPB1 3 1 0 0\n"));
                var deadline=Stopwatch.StartNew();
                while(evidence.Last is null)
                {if(deadline.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException("Evidence reader captured the blocked dispatcher.");Thread.Sleep(10);}
                Assert.Equal(BrokerStage.BootstrapOwner,evidence.Last.Stage);
                // OnExit blocks STA while a background caller disposes the broker.
                Task.Run(async()=>await evidence.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                finished.TrySetResult();
            }
            catch(Exception exception){finished.TrySetException(exception);}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }
}
