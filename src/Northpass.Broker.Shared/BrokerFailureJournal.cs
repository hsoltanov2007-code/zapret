using System.Text.Json;
using Northpass.Services.Installation;

namespace Northpass.Broker;

// Local advisory evidence only: never read for authorization, installation or
// automatic recovery. One atomic overwritten record, not an unbounded history.
public static class BrokerFailureJournal
{
    public static void Save(string folder,BrokerFailure failure,IReadOnlyList<BrokerEvidenceRecord> records)
    {
        if(records.Count>64)throw new InvalidDataException("Broker journal exceeds record bounds.");
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(new {Version=1,RecordedUtc=DateTimeOffset.UtcNow,Failure=failure,Stages=records});
        if(bytes.Length>16384)throw new InvalidDataException("Broker journal exceeds byte bounds.");
        SafeArchive.NoLinks(folder);Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"broker-startup.json");SafeArchive.NoLinks(path);
        string temporary=Path.Combine(folder,"broker-startup-"+Guid.NewGuid().ToString("N")+".tmp");
        try {using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush();}File.Move(temporary,path,true);}
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
}
