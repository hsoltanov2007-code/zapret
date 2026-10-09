using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using Northpass.Engine.Zapret1;
using Northpass.Models;
using Northpass.Services;

namespace Northpass.Tests;

public sealed class TelegramTests
{
    [Fact]
    public void OfficialBootstrapIsExactAndDoesNotTrustRangesTestDcsOrOtherPorts()
    {
        Assert.Equal(11, TelegramEndpoints.Bootstrap.Count);
        Assert.Equal(6, TelegramEndpoints.Bootstrap.Count(e=>IPAddress.Parse(e.Address).AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork));
        Assert.True(TelegramEndpoints.Matches(IPAddress.Parse("149.154.175.50"),443));
        Assert.True(TelegramEndpoints.Matches(IPAddress.Parse("2001:b28:f23d:f001::a"),80));
        Assert.False(TelegramEndpoints.Matches(IPAddress.Parse("149.154.175.51"),443));
        Assert.False(TelegramEndpoints.Matches(IPAddress.Parse("149.154.175.10"),443));
        Assert.False(TelegramEndpoints.Matches(IPAddress.Parse("149.154.175.50"),5222));
        Assert.DoesNotContain('/', TelegramEndpoints.IpSet);
    }
    [Theory]
    [InlineData("general")][InlineData("alt")][InlineData("alt2")][InlineData("simple-fake")][InlineData("fake-tls-auto")]
    public void AllOriginalYoutubeDiscordRulesGlobalsAndSavedProfileTemplatesStayIdentical(string id)
    {
        var s=FlowsealCatalog.Find(id)!;
        var original=FlowsealCatalog.Compile(s,"bin","lists","data","12","12",includeTelegram:false);
        var actual=FlowsealCatalog.Compile(s,"bin","lists","data","12","12");
        int globals=s.Global.Count;
        Assert.Equal(original.Take(globals),actual.Take(globals));
        Assert.Equal(FlowsealCatalog.TelegramRule(),actual.Skip(globals).Take(FlowsealCatalog.TelegramRule().Count));
        Assert.Equal(original.Skip(globals),actual.Skip(globals+FlowsealCatalog.TelegramRule().Count+3));
        Assert.Contains("--ipset-exclude=data/ipset-exclude-user.txt",actual);
        Assert.Contains("--ipset-exclude=lists/ipset-exclude.txt",actual);
        Assert.Equal(8,FlowsealCatalog.Profile(s).Arguments.Count(a=>a=="--new"));
        Assert.DoesNotContain(actual,a=>a.StartsWith("--wf-raw"));
        Assert.DoesNotContain(FlowsealCatalog.TelegramRule(),a=>a.StartsWith("--filter-udp") || a.Contains("fake") || a.Contains("seqovl"));
        Assert.Contains("--dpi-desync-cutoff=d2",FlowsealCatalog.TelegramRule());
    }
    private static byte[] Reply(byte[] nonce)
    {
        var p=new byte[80];p[8]=1;BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(16),60);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(20),0x05162463);nonce.CopyTo(p,24);
        p[56]=8;BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(68),0x1cb5c415);BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(72),1);
        // 8-byte pq pads to 12 -> vector68 +8+8 =84, not80.
        Array.Resize(ref p,84);BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(16),64);return p;
    }
    [Fact]
    public void BoundedNonceBoundResponseRejectsForgedMalformedAndOversizedReplies()
    {
        byte[] nonce=Enumerable.Range(0,16).Select(i=>(byte)i).ToArray(),p=Reply(nonce);
        TelegramProbeSession.ValidateResponse(p,nonce);
        var bad=(byte[])p.Clone();bad[24]^=1;Assert.Throws<InvalidDataException>(()=>TelegramProbeSession.ValidateResponse(bad,nonce));
        foreach(int length in new[]{0,1,19,79,513,65536})Assert.Throws<InvalidDataException>(()=>TelegramProbeSession.ValidateResponse(new byte[length],nonce));
        foreach(int offset in new[]{0,8,16,20,56,68,72}) {
            bad=(byte[])p.Clone();bad[offset]^=0xff;var copy=bad;
            Assert.Throws<InvalidDataException>(()=>TelegramProbeSession.ValidateResponse(copy,nonce));
        }
    }
    [Fact]
    public async Task CancelledRealSocketProbeDoesNotReturnSuccessOrContactArbitraryEndpoint()
    {
        using var c=new CancellationTokenSource();c.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>TelegramProbe.ProbeAsync(TelegramEndpoints.Bootstrap[0],TelegramTransport.Abridged,c.Token));
        await Assert.ThrowsAsync<ArgumentException>(()=>TelegramProbe.ProbeAsync(new(1,"127.0.0.1",443),TelegramTransport.Abridged,default));
    }
    [Theory]
    [InlineData(TelegramTransport.Abridged)]
    [InlineData(TelegramTransport.ObfuscatedAbridged)]
    public async Task SyntheticAbridgedServerChecksHarmlessRequestAndValidatesInitialReply(TelegramTransport transport)
    {
        // Explicit memory-stream fixture, not a real Telegram server/network.
        using var session=new TelegramProbeSession(1,transport);
        using var stream=new ReplyStream();await session.ExchangeAsync(stream,default);
        Assert.Equal(transport == TelegramTransport.Abridged ? 42 : 105,stream.Request!.Length);
        Assert.Equal(0x60469778u,BinaryPrimitives.ReadUInt32LittleEndian(stream.Decoded!.AsSpan(21)));
        Assert.All(stream.Decoded!.Skip(1).Take(8),x=>Assert.Equal(0,x));
    }
    private sealed class ReplyStream : Stream
    {
        public byte[]? Request,Decoded; private MemoryStream? response;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default)
        {
            Request=buffer.ToArray();
            byte[]? reversed=null;
            if(Request.Length==42) { Assert.Equal(0xef,Request[0]);Decoded=Request.AsSpan(1).ToArray(); }
            else {
                // Independently emulate the synthetic server's AES-CTR stream.
                byte[] encrypted=Request.ToArray();var key=Request.AsSpan(8,32).ToArray();var iv=Request.AsSpan(40,16).ToArray();
                reversed=Request.AsSpan(8,48).ToArray();Array.Reverse(reversed);
                Ctr(encrypted,key,iv);Assert.Equal(0xefefefefu,BinaryPrimitives.ReadUInt32LittleEndian(encrypted.AsSpan(56)));
                Assert.Equal(1,BinaryPrimitives.ReadInt16LittleEndian(encrypted.AsSpan(60)));Decoded=encrypted.AsSpan(64).ToArray();
            }
            byte[] nonce=Decoded.AsSpan(25,16).ToArray(),p=Reply(nonce);byte[] framed=new[]{(byte)(p.Length/4)}.Concat(p).ToArray();
            if(reversed!=null)Ctr(framed,reversed.AsSpan(0,32).ToArray(),reversed.AsSpan(32,16).ToArray());
            response=new MemoryStream(framed);return ValueTask.CompletedTask;
        }
        private static void Ctr(byte[] data,byte[] key,byte[] iv)
        {
            using var aes=Aes.Create();aes.Key=key;
            for(int offset=0;offset<data.Length;offset+=16) {
                byte[] block=aes.EncryptEcb(iv,PaddingMode.None);
                for(int j=0;j<16 && offset+j<data.Length;j++)data[offset+j]^=block[j];
                for(int j=15;j>=0;j--)if(++iv[j]!=0)break;
            }
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)=>response!.ReadAsync(buffer,token);
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();
    }
}
