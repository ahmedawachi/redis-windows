using RedisService.Service;

namespace RedisService.Tests;

public class EndpointTests
{
    private static readonly ConfPathContext Ctx = new(@"C:\Redis", @"C:\Redis", RuntimeFlavor.Msys2);

    private static RedisEndpoint? From(string conf, string? password = null) =>
        RedisEndpoint.FromConfig(RedisConfFile.Parse(conf, Ctx), Ctx, password, null);

    [Fact]
    public void Defaults_Loopback6379()
    {
        var e = From("");
        Assert.NotNull(e);
        Assert.Equal("127.0.0.1", e.Host);
        Assert.Equal(6379, e.Port);
        Assert.False(e.UseTls);
    }

    [Theory]
    [InlineData("bind 127.0.0.1 -::1", "127.0.0.1")]
    [InlineData("bind * -::*", "127.0.0.1")]
    [InlineData("bind 0.0.0.0", "127.0.0.1")]
    [InlineData("bind 10.1.2.3", "10.1.2.3")]
    [InlineData("bind 10.1.2.3 127.0.0.1", "127.0.0.1")]
    [InlineData("bind ::1", "::1")]
    [InlineData("bind -10.9.9.9", "10.9.9.9")]
    [InlineData("bind 127.0.0.2", "127.0.0.2")]
    [InlineData("bind 10.1.2.3 -127.0.0.5", "127.0.0.5")]
    [InlineData("bind localhost", "127.0.0.1")]
    [InlineData("bind 10.1.2.3 ::", "::1")]
    public void Bind_PicksAReachableAddress(string conf, string host) => Assert.Equal(host, From(conf)!.Host);

    [Fact]
    public void TlsOnly_UsesTlsPortAndFiles()
    {
        var e = From("port 0\ntls-port 6380\ntls-cert-file certs/redis.crt\ntls-key-file certs/redis.key\ntls-ca-cert-file C:/certs/ca.crt\n");
        Assert.NotNull(e);
        Assert.Equal(6380, e.Port);
        Assert.True(e.UseTls);
        Assert.Equal(@"C:\Redis\certs\redis.crt", e.Tls!.CertFile);
        Assert.Equal(@"C:\Redis\certs\redis.key", e.Tls.KeyFile);
        Assert.Equal(@"C:\certs\ca.crt", e.Tls.CaCertFile);
        Assert.True(e.Tls.ClientCertificateRequired);
    }

    [Fact]
    public void TlsClientCertificateFiles_TakePrecedence_AndAuthClientsNo()
    {
        var e = From("port 0\ntls-port 6380\ntls-cert-file s.crt\ntls-key-file s.key\ntls-client-cert-file c.crt\ntls-client-key-file c.key\ntls-auth-clients no\n");
        Assert.Equal(@"C:\Redis\c.crt", e!.Tls!.CertFile);
        Assert.False(e.Tls.ClientCertificateRequired);
    }

    [Fact]
    public void PlainPortPreferredOverTls() => Assert.False(From("port 6379\ntls-port 6380\n")!.UseTls);

    [Fact]
    public void NoTcpListener_ReturnsNull() => Assert.Null(From("port 0\nunixsocket /tmp/redis.sock\n"));

    [Fact]
    public void Password_NeverAppearsInToString()
    {
        var e = From("", password: "s3cret-value")!;
        Assert.DoesNotContain("s3cret-value", e.ToString());
        Assert.DoesNotContain("s3cret-value", $"{e}");
    }
}
