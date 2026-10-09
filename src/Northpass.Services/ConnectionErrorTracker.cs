namespace Northpass.Services;

// One last fingerprint, not an ever-growing history. Shared by explicit failure
// callbacks and status polling so their order cannot duplicate an error event.
public sealed class ConnectionErrorTracker
{
    private string? _last;
    public void BeginAttempt()=>_last=null;
    public bool Observe(string? error)
    {
        if(error is null){_last=null;return false;}
        if(error==_last)return false;
        _last=error;return true;
    }
}
