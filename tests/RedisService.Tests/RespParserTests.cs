using System.Text;
using RedisService.Service;

namespace RedisService.Tests;

public class RespParserTests
{
    private sealed class ScriptedStream(string response, int chunk = int.MaxValue) : Stream
    {
        private readonly MemoryStream _in = new(Encoding.UTF8.GetBytes(response));
        public readonly MemoryStream Written = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, Math.Min(count, chunk));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }

    private static async Task<RespReply> Reply(string wire, int chunk = int.MaxValue)
    {
        using var c = new RespConnection(new ScriptedStream(wire, chunk));
        return await c.ReadReplyAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Encode_IsARespArrayOfBulkStrings() =>
        Assert.Equal("*2\r\n$4\r\nAUTH\r\n$6\r\npäss!\r\n".Replace("päss!", "päss!"),
            Encoding.UTF8.GetString(RespConnection.Encode(["AUTH", "päss!"])));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4096)]
    public async Task SimpleString(int chunk)
    {
        var r = await Reply("+PONG\r\n", chunk);
        Assert.Equal(RespType.SimpleString, r.Type);
        Assert.Equal("PONG", r.Text);
    }

    [Fact]
    public async Task Error_ExposesItsCode()
    {
        var r = await Reply("-LOADING Redis is loading the dataset in memory\r\n");
        Assert.True(r.IsError);
        Assert.Equal("LOADING", r.ErrorCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public async Task BulkIntegerNullAndArray(int chunk)
    {
        var r = await Reply("*4\r\n$5\r\nhe\r\no\r\n:-42\r\n$-1\r\n*-1\r\n", chunk);
        Assert.Equal(RespType.Array, r.Type);
        Assert.Equal("he\r\no", r.Items![0].Text);
        Assert.Equal(-42, r.Items[1].Integer);
        Assert.Equal(RespType.Null, r.Items[2].Type);
        Assert.Equal(RespType.Null, r.Items[3].Type);
    }

    [Fact]
    public async Task ClosedBeforeAnyByte_IsConnectionClosed() =>
        await Assert.ThrowsAsync<RespConnectionClosedException>(() => Reply(""));

    [Theory]
    [InlineData("+PON")]
    [InlineData("$10\r\nshort\r\n")]
    [InlineData("?what\r\n")]
    [InlineData(":notanumber\r\n")]
    [InlineData("$3\r\nabcXY")]
    public async Task Malformed_IsAProtocolError(string wire) =>
        await Assert.ThrowsAsync<RespProtocolException>(() => Reply(wire));

    [Fact]
    public async Task OversizedBulk_IsRejectedWithoutAllocating() =>
        await Assert.ThrowsAsync<RespProtocolException>(() => Reply("$999999999999\r\n"));

    [Fact]
    public async Task Execute_WritesTheCommand()
    {
        var stream = new ScriptedStream("+OK\r\n");
        using var c = new RespConnection(stream);
        var r = await c.ExecuteAsync(["PING"], TestContext.Current.CancellationToken);
        Assert.Equal("OK", r.Text);
        Assert.Equal("*1\r\n$4\r\nPING\r\n", Encoding.UTF8.GetString(stream.Written.ToArray()));
    }
}
