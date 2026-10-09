using Northpass.Broker;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
namespace Northpass.Tests;
public sealed class BrokerProtocolTests
{
    [Theory]
    [InlineData(2,1,"STOP","native")]
    [InlineData(3,0,"STOP","native")]
    [InlineData(3,2,"STOP","native")]
    [InlineData(3,1,"SHELL","native")]
    [InlineData(3,1,"STOP","unknown")]
    public void CommandsAndReplayFailClosed(int version,uint sequence,string command,string engine) =>
        Assert.Throws<InvalidDataException>(()=>BrokerProtocol.Validate(new(version,sequence,command,engine),1));
    [Fact] public void ScopeCannotBecomeGeneralNetworkInterception()
    {
        BrokerProtocol.Validate(new(3,1,"START","native","passthrough-loopback",55001,"udp"),1);
        Assert.Throws<InvalidDataException>(()=>BrokerProtocol.Validate(new(3,1,"START","native","passthrough-loopback",443,"udp"),1));
        Assert.Throws<InvalidDataException>(()=>BrokerProtocol.Validate(new(3,1,"START","native","passthrough-idle",55001),1));
        Assert.Throws<InvalidDataException>(()=>BrokerProtocol.Validate(new(3,1,"STOP","native","passthrough-idle"),1));
    }
    [Theory]
    [InlineData("{\"Version\":3,\"Version\":2}")]
    [InlineData("{\"Version\":3,\"RawCommand\":\"cmd.exe\"}")]
    public async Task DuplicateAndUnknownFieldsAreRejected(string json)
    {
        byte[] body=Encoding.UTF8.GetBytes(json),header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,body.Length);
        using var stream=new MemoryStream();stream.Write(header);stream.Write(body);stream.Position=0;
        await Assert.ThrowsAnyAsync<Exception>(()=>BrokerProtocol.ReadAsync<BrokerRequest>(stream,default));
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(8388609)]
    public async Task LengthIsCheckedBeforeAllocation(int length)
    {
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,length);
        await Assert.ThrowsAsync<InvalidDataException>(()=>BrokerProtocol.ReadAsync<BrokerRequest>(new MemoryStream(header),default));
    }
    [Fact] public async Task TypedMessageRoundTrips()
    {
        using var stream=new MemoryStream();var request=new BrokerRequest(3,1,"START","native","passthrough-idle");
        await BrokerProtocol.WriteAsync(stream,request,default);stream.Position=0;
        var read=await BrokerProtocol.ReadAsync<BrokerRequest>(stream,default);BrokerProtocol.Validate(read,1);Assert.Equal(request,read);
    }
    [Fact] public void WindowsCancelledElevationHasExplicitRetryableError()
    {
        // Error mapping only. This does not simulate or validate secure-desktop UAC.
        Assert.IsType<ElevationDeclinedException>(BrokerClient.ElevationError(new Win32Exception(1223)));
        Assert.IsType<Win32Exception>(BrokerClient.ElevationError(new Win32Exception(5)));
    }
}
