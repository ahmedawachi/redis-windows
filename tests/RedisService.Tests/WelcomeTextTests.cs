using RedisService.Service;

namespace RedisService.Tests;

public class WelcomeTextTests
{
    private static SupervisorSettings Settings(RedisEndpoint? endpoint) => new()
    {
        RedisServerPath = Path.Combine("C:", "Redis", "redis-server.exe"),
        WorkingDirectory = "",
        Arguments = [],
        Flavor = RuntimeFlavor.Native,
        ConfigFilePath = "C:/Redis/redis.conf",
        DataDirectory = "C:/Redis/data",
        Endpoint = endpoint,
    };

    private static string ConnectLine(RedisEndpoint? endpoint, bool windows = true) =>
        Assert.Single(Program.WelcomeLines(Settings(endpoint), windows), l => l.StartsWith("  Connect"));

    [Fact]
    public void DefaultLoopbackPort_NeedsNoArguments() =>
        Assert.EndsWith("redis-cli.exe\"", ConnectLine(new RedisEndpoint("127.0.0.1", 6379, null, null, null)));

    [Fact]
    public void OtherPort_AddsDashP() =>
        Assert.EndsWith("redis-cli.exe\" -p 6380", ConnectLine(new RedisEndpoint("127.0.0.1", 6380, null, null, null)));

    [Fact]
    public void OtherHost_AddsDashH() =>
        Assert.EndsWith("redis-cli.exe\" -h ::1", ConnectLine(new RedisEndpoint("::1", 6379, null, null, null)));

    [Fact]
    public void Tls_AddsTheTlsFlag() =>
        Assert.Contains(" --tls", ConnectLine(new RedisEndpoint("127.0.0.1", 6379, new RedisTlsSettings(null, null, null, null, false), null, null)));

    [Fact]
    public void NoTcpPort_SaysSoInsteadOfACommand() =>
        Assert.Contains("no TCP port is configured", ConnectLine(null));

    [Theory]
    [InlineData(true, "Redis for Windows is starting in this window.", "redis-cli.exe")]
    [InlineData(false, "Redis is starting in this window.", "redis-cli\"")]
    public void TitleAndCliNameFollowThePlatform(bool windows, string title, string cli)
    {
        var lines = Program.WelcomeLines(Settings(new RedisEndpoint("127.0.0.1", 6379, null, null, null)), windows);
        Assert.Equal(title, lines[0]);
        Assert.Contains(cli, lines.Single(l => l.StartsWith("  Connect")));
    }
}
