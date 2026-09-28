using RedisService.CommandLine;
using RedisService.Service;

namespace RedisService.Tests;

public class SettingsResolverTests
{
    private static string FakeServer(TempDir dir)
    {
        var path = dir.File("redis-server", "#!/bin/sh\n");
        return path;
    }

    [Fact]
    public void MissingRedisServer_IsAConfigurationError()
    {
        using var dir = new TempDir();
        var conf = dir.File("redis.conf", "port 6379\n");
        var ex = Assert.Throws<WrapperConfigurationException>(() => SupervisorSettingsResolver.Resolve(
            new RunOptions { ConfigFilePath = conf, RedisServerPath = Path.Combine(dir.Path, "nope") }, dir.Path, dir.Path));
        Assert.Contains("redis-server was not found", ex.Message);
    }

    [Fact]
    public void MissingConfig_IsAConfigurationError()
    {
        using var dir = new TempDir();
        var ex = Assert.Throws<WrapperConfigurationException>(() => SupervisorSettingsResolver.Resolve(
            new RunOptions { RedisServerPath = FakeServer(dir) }, dir.Path, dir.Path));
        Assert.Contains(Path.Combine(dir.Path, "redis.conf"), ex.Message); // Default: next to the exe, not the cwd.
    }

    [Fact]
    public void Overrides_BecomeRedisArguments_AndDriveTheEndpoint()
    {
        using var dir = new TempDir();
        var conf = dir.File("redis.conf", "port 6379\nrequirepass fromconf\nlogfile redis.log\ndir ./data\n");
        Directory.CreateDirectory(Path.Combine(dir.Path, "other"));
        var s = SupervisorSettingsResolver.Resolve(new RunOptions
        {
            ConfigFilePath = "redis.conf",
            RedisServerPath = FakeServer(dir),
            Port = 7001,
            DataDirectory = "other/",
            LogLevel = "warning",
        }, dir.Path, dir.Path);

        Assert.Equal([conf, "--port", "7001", "--dir", Path.Combine(dir.Path, "other"), "--loglevel", "warning"], s.Arguments);
        Assert.Equal(7001, s.Endpoint!.Port);
        Assert.Equal("fromconf", s.Endpoint.Password);
        Assert.Equal(Path.Combine(dir.Path, "other"), s.DataDirectory);
        Assert.Equal(Path.Combine(dir.Path, "other", "redis.log"), s.LogFilePath); // Relative logfile follows dir.
        Assert.Equal(RuntimeFlavor.Native, s.Flavor);
        Assert.Equal(dir.Path, s.WorkingDirectory);
    }

    [Fact]
    public void PasswordFile_WinsOverRequirepass_AndStdoutLogfileIsNull()
    {
        using var dir = new TempDir();
        dir.File("redis.conf", "requirepass fromconf\nlogfile \"\"\n");
        var pw = dir.File("pw.txt", "fromfile\r\n");
        var s = SupervisorSettingsResolver.Resolve(new RunOptions
        {
            RedisServerPath = FakeServer(dir),
            AuthPasswordFile = pw,
            AuthUser = "svc",
        }, dir.Path, dir.Path);
        Assert.Equal("fromfile", s.Endpoint!.Password);
        Assert.Equal("svc", s.Endpoint.User);
        Assert.Null(s.LogFilePath);
    }

    [Fact]
    public void MissingPasswordFile_IsAConfigurationError()
    {
        using var dir = new TempDir();
        dir.File("redis.conf", "");
        Assert.Throws<WrapperConfigurationException>(() => SupervisorSettingsResolver.Resolve(
            new RunOptions { RedisServerPath = FakeServer(dir), AuthPasswordFile = "missing.txt" }, dir.Path, dir.Path));
    }

    [Fact]
    public void NoTcpPort_WarnsThatProbingIsUnavailable()
    {
        using var dir = new TempDir();
        dir.File("redis.conf", "port 0\n");
        var s = SupervisorSettingsResolver.Resolve(new RunOptions { RedisServerPath = FakeServer(dir) }, dir.Path, dir.Path);
        Assert.Null(s.Endpoint);
        Assert.Contains(s.Warnings, w => w.Contains("no TCP listener"));
    }

    [Fact]
    public void ProbeTimeout_NeverExceedsTheInterval()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), new SupervisorSettings { RedisServerPath = "", WorkingDirectory = "", Arguments = [], Flavor = RuntimeFlavor.Native, ConfigFilePath = "", DataDirectory = "" }.ProbeTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(500), new SupervisorSettings { RedisServerPath = "", WorkingDirectory = "", Arguments = [], Flavor = RuntimeFlavor.Native, ConfigFilePath = "", DataDirectory = "", HealthInterval = TimeSpan.FromMilliseconds(500) }.ProbeTimeout);
    }

    [Fact]
    public void AclUsersWithoutAPassword_Warn()
    {
        using var dir = new TempDir();
        var conf = dir.File("redis.conf", "port 6379\nuser default on >pw ~* &* +@all\n");
        var s = SupervisorSettingsResolver.Resolve(new RunOptions { ConfigFilePath = conf, RedisServerPath = FakeServer(dir) }, dir.Path, dir.Path);
        Assert.Contains(s.Warnings, w => w.Contains("ACL users") && w.Contains("--auth-password-file"));

        var withPass = dir.File("with-pass.conf", "port 6379\nrequirepass pw\naclfile users.acl\n");
        var t = SupervisorSettingsResolver.Resolve(new RunOptions { ConfigFilePath = withPass, RedisServerPath = FakeServer(dir) }, dir.Path, dir.Path);
        Assert.DoesNotContain(t.Warnings, w => w.Contains("ACL users"));
    }

    [Fact]
    public void UnloadableTlsClientCertificate_Warns()
    {
        using var dir = new TempDir();
        var conf = dir.File("redis.conf", "port 0\ntls-port 6380\ntls-cert-file missing.crt\ntls-key-file missing.key\n");
        var s = SupervisorSettingsResolver.Resolve(new RunOptions { ConfigFilePath = conf, RedisServerPath = FakeServer(dir) }, dir.Path, dir.Path);
        Assert.Contains(s.Warnings, w => w.Contains("cannot load the client certificate"));
    }
}
