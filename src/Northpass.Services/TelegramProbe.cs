using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Northpass.Models;

namespace Northpass.Services;

public enum TelegramTransport { Abridged, ObfuscatedAbridged }
public enum TelegramProbeState { InitialResponse, Timeout, TcpError, InvalidResponse, Closed }
public sealed record TelegramProbeResult(int Dc, string Family, TelegramTransport Transport,
    TelegramProbeState State, string Stage, int SafeCode, long ElapsedMilliseconds, bool ConfiguredRuleMatch);

// Explicit opt-in only. Fixed public bootstrap endpoints, no account login,
// proxy reads, DNS, credentials, application data or payload retention.
public static class TelegramProbe
{
    public static async Task<TelegramProbeResult> ProbeAsync(TelegramEndpoint endpoint, TelegramTransport transport, CancellationToken token)
    {
        if (!TelegramEndpoints.Bootstrap.Contains(endpoint) || !Enum.IsDefined(transport)) throw new ArgumentException("Only reviewed Telegram probe endpoints/transports allowed.");
        token.ThrowIfCancellationRequested();
        var ip = IPAddress.Parse(endpoint.Address); var watch = Stopwatch.StartNew(); string stage = "TCP";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var client = new TcpClient(ip.AddressFamily);
        using var session = new TelegramProbeSession(endpoint.Dc, transport);
        TelegramProbeState state; int code = 0;
        try
        {
            await client.ConnectAsync(ip, endpoint.Port, deadline.Token); stage = "MTProto";
            await session.ExchangeAsync(client.GetStream(), deadline.Token); state = TelegramProbeState.InitialResponse;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { state = TelegramProbeState.Timeout; }
        catch (SocketException ex) { state = TelegramProbeState.TcpError; code = (int)ex.SocketErrorCode; }
        catch (EndOfStreamException) { state = TelegramProbeState.Closed; }
        catch (InvalidDataException) { state = TelegramProbeState.InvalidResponse; }
        catch (IOException) { state = TelegramProbeState.TcpError; }
        token.ThrowIfCancellationRequested();
        return new(endpoint.Dc, ip.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6", transport, state, stage, code,
            watch.ElapsedMilliseconds, TelegramEndpoints.Matches(ip, endpoint.Port));
    }
}

// Original bounded wire codec for a harmless unencrypted req_pq and a nonce-
// bound resPQ. Obfuscation is transport encryption, NOT server authentication.
public sealed class TelegramProbeSession : IDisposable
{
    private readonly byte[] nonce = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] request;
    private readonly CounterCipher? send, receive;
    public TelegramProbeSession(int dc, TelegramTransport transport)
    {
        if (dc is <1 or >5 || !Enum.IsDefined(transport)) throw new ArgumentException("Invalid probe configuration.");
        byte[] frame = new byte[41]; frame[0] = 10;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(9), (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() << 32);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(17), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(21), 0x60469778); nonce.CopyTo(frame, 25);
        if (transport == TelegramTransport.Abridged) { request = new byte[42]; request[0] = 0xef; frame.CopyTo(request, 1); }
        else
        {
            byte[] header;
            do { header = RandomNumberGenerator.GetBytes(64); }
            while (header[0] is 0xef or 0x16 || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) == 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header) is 0x44414548 or 0x54534f50 or 0x20544547 or 0x4954504f or 0xdddddddd or 0xeeeeeeee);
            byte[] reverse = header.AsSpan(8,48).ToArray(); Array.Reverse(reverse);
            send = new(header.AsSpan(8,32), header.AsSpan(40,16)); receive = new(reverse.AsSpan(0,32), reverse.AsSpan(32,16));
            CryptographicOperations.ZeroMemory(reverse);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), 0xefefefef); BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(60), (short)dc);
            request = new byte[105]; header.AsSpan(0,56).CopyTo(request); send.Apply(header); header.AsSpan(56,8).CopyTo(request.AsSpan(56));
            send.Apply(frame); frame.CopyTo(request,64); CryptographicOperations.ZeroMemory(header);
        }
        CryptographicOperations.ZeroMemory(frame);
    }
    public async Task ExchangeAsync(Stream stream, CancellationToken token)
    {
        await stream.WriteAsync(request,token); await stream.FlushAsync(token);
        byte[] prefix = new byte[1]; await ReadAsync(stream,prefix,token); int words = prefix[0];
        if (words == 0x7f)
        {
            byte[] extra = new byte[3]; await ReadAsync(stream,extra,token);
            words = extra[0] | extra[1]<<8 | extra[2]<<16;
            if (words < 127) throw new InvalidDataException("Noncanonical MTProto frame.");
        }
        if (words is < 19 or > 128) throw new InvalidDataException("MTProto frame exceeds probe bounds.");
        byte[] response = new byte[words*4];
        try { await ReadAsync(stream,response,token); ValidateResponse(response,nonce); }
        finally { CryptographicOperations.ZeroMemory(response); }
    }
    private async Task ReadAsync(Stream stream, byte[] bytes, CancellationToken token)
    { await stream.ReadExactlyAsync(bytes,token); receive?.Apply(bytes); }
    public static void ValidateResponse(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length !=16 || packet.Length is <76 or >512 || packet.Length%4!=0 ||
            BinaryPrimitives.ReadUInt64LittleEndian(packet)!=0 || (packet[8]&3) is not (1 or 3) ||
            BinaryPrimitives.ReadInt32LittleEndian(packet[16..]) != packet.Length-20 ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet[20..])!=0x05162463 || !CryptographicOperations.FixedTimeEquals(packet.Slice(24,16),nonce))
            throw new InvalidDataException("Invalid MTProto response.");
        // resPQ: nonce, server_nonce, <=8 byte pq, bounded vector<long>.
        int pq = packet[56]; if (pq is <1 or >8) throw new InvalidDataException("Invalid pq size.");
        int vector = 56 + ((pq+1+3)/4)*4;
        if (vector+8>packet.Length || BinaryPrimitives.ReadUInt32LittleEndian(packet[vector..])!=0x1cb5c415)
            throw new InvalidDataException("Invalid fingerprint vector.");
        int count=BinaryPrimitives.ReadInt32LittleEndian(packet[(vector+4)..]);
        if(count is <1 or >16 || vector+8+count*8!=packet.Length) throw new InvalidDataException("Invalid fingerprint count.");
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(nonce); CryptographicOperations.ZeroMemory(request); send?.Dispose();receive?.Dispose(); }
    private sealed class CounterCipher : IDisposable
    {
        private readonly Aes aes; private readonly ICryptoTransform encryptor; private readonly byte[] counter, block=new byte[16]; private int used=16;
        public CounterCipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
        { aes=Aes.Create();aes.Key=key.ToArray();aes.Mode=CipherMode.ECB;aes.Padding=PaddingMode.None;encryptor=aes.CreateEncryptor();counter=iv.ToArray(); }
        public void Apply(Span<byte> bytes)
        {
            for(int i=0;i<bytes.Length;i++) {
                if(used==16) { encryptor.TransformBlock(counter,0,16,block,0);for(int j=15;j>=0;j--)if(++counter[j]!=0)break;used=0; }
                bytes[i]^=block[used++];
            }
        }
        public void Dispose(){encryptor.Dispose();aes.Dispose();CryptographicOperations.ZeroMemory(counter);CryptographicOperations.ZeroMemory(block);}
    }
}
