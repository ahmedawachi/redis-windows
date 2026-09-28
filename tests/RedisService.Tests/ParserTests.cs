using RedisService.CommandLine;

namespace RedisService.Tests;

public class ParserTests
{
    [Fact]
    public void NoArguments_RunsWithTheDefaults()
    {
        // Not forced to the foreground: a service created with a bare ImagePath must still start as a service.
        var run = Assert.IsType<RunCommand>(CommandLineParser.Parse([]));
        Assert.False(run.Options.Foreground);
        Assert.Null(run.Options.ConfigFilePath); // redis.conf next to RedisService.exe
        Assert.Equal("Redis", run.Options.ServiceName);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("install", "--help")]
    public void HelpFlag_AnywhereShowsHelp(params string[] args) => Assert.IsType<HelpCommand>(CommandLineParser.Parse(args));

    [Fact]
    public void VersionFlag() => Assert.IsType<VersionCommand>(CommandLineParser.Parse(["-v"]));

    [Fact]
    public void LegacyImagePath_StillParses()
    {
        // What RedisService 2.0.0 wrote into ImagePath.
        var cmd = Assert.IsType<RunCommand>(CommandLineParser.Parse(
            ["run", "-c", @"C:\Redis\redis.conf", "--port", "6380", "--dir", @"D:\data", "--loglevel", "notice"]));
        Assert.Equal(@"C:\Redis\redis.conf", cmd.Options.ConfigFilePath);
        Assert.Equal(6380, cmd.Options.Port);
        Assert.Equal(@"D:\data", cmd.Options.DataDirectory);
        Assert.Equal("notice", cmd.Options.LogLevel);
        Assert.False(cmd.Options.Foreground);
    }

    [Fact]
    public void RunIsTheDefaultCommand()
    {
        var cmd = Assert.IsType<RunCommand>(CommandLineParser.Parse(["--foreground", "--config", "x.conf"]));
        Assert.True(cmd.Options.Foreground);
        Assert.Equal("x.conf", cmd.Options.ConfigFilePath);
    }

    [Fact]
    public void OptionNamesAreCaseInsensitive()
    {
        var cmd = Assert.IsType<RunCommand>(CommandLineParser.Parse(["RUN", "--PORT", "7000", "-F"]));
        Assert.Equal(7000, cmd.Options.Port);
        Assert.True(cmd.Options.Foreground);
    }

    [Fact]
    public void InlineValueSyntax()
    {
        var cmd = Assert.IsType<RunCommand>(CommandLineParser.Parse(["run", "--port=6390", "--stop-timeout=2m"]));
        Assert.Equal(6390, cmd.Options.Port);
        Assert.Equal(TimeSpan.FromMinutes(2), cmd.Options.StopTimeout);
    }

    [Theory]
    [InlineData("run", "--servicename", "X")]
    [InlineData("run", "--bogus")]
    [InlineData("install", "--foreground")]
    [InlineData("uninstall", "--port", "1")]
    [InlineData("instal")]
    [InlineData("run", "stray")]
    [InlineData("run", "--port")]
    [InlineData("run", "--port", "abc")]
    [InlineData("run", "--port", "70000")]
    [InlineData("run", "--port", "-1")]
    [InlineData("run", "--loglevel", "loud")]
    [InlineData("run", "--restart-policy", "sometimes")]
    [InlineData("run", "--stop-timeout", "0")]
    [InlineData("run", "--stop-timeout", "soon")]
    [InlineData("run", "--health-failures", "0")]
    [InlineData("run", "--foreground=yes")]
    [InlineData("install", "--start-mode", "boot")]
    [InlineData("install", "--start-mode", "manual", "--delayed-start")]
    [InlineData("run", "--service-name", "a/b")]
    [InlineData("run", "--logfile", "")]
    public void InvalidInput_IsRejected(params string[] args)
    {
        var ex = Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(args));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Equal(2, CommandLineException.ExitCode);
    }

    [Fact]
    public void Install_AllOptions()
    {
        var cmd = Assert.IsType<InstallCommand>(CommandLineParser.Parse(
            ["install", "--service-name", "RedisCI", "--display-name", "Redis CI", "--description", "d", "--start-mode", "auto",
             "--delayed-start", "--virtual-account", "--restart-policy", "always", "--stop-timeout", "90s", "--port", "6390",
             "--auth-password-file", "pw.txt", "--auth-user", "svc", "--shutdown-force", "--max-restarts", "3", "--restart-window", "5m",
             "--health-interval", "0", "--health-failures", "4", "--start-timeout", "30"]));
        var o = cmd.Options;
        Assert.Equal("RedisCI", o.ServiceName);
        Assert.Equal("Redis CI", o.DisplayName);
        Assert.True(o.DelayedStart);
        Assert.True(o.VirtualAccount);
        Assert.Equal(RestartPolicy.Always, o.RestartPolicy);
        Assert.Equal(TimeSpan.FromSeconds(90), o.StopTimeout);
        Assert.Equal(6390, o.Port);
        Assert.Equal("svc", o.AuthUser);
        Assert.True(o.ShutdownForce);
        Assert.Equal(3, o.MaxRestarts);
        Assert.Equal(TimeSpan.FromMinutes(5), o.RestartWindow);
        Assert.Equal(TimeSpan.Zero, o.HealthInterval);
        Assert.Equal(4, o.HealthFailures);
        Assert.Equal(TimeSpan.FromSeconds(30), o.StartTimeout);
    }

    [Fact]
    public void Defaults_MatchTheRoadmap()
    {
        var o = new RunOptions();
        Assert.Equal(RestartPolicy.OnCrash, o.RestartPolicy);
        Assert.Equal(TimeSpan.FromSeconds(120), o.StopTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), o.StartTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), o.HealthInterval);
        Assert.Equal(12, o.HealthFailures);
        Assert.Equal(5, o.MaxRestarts);
        Assert.Equal(TimeSpan.FromMinutes(10), o.RestartWindow);
        Assert.False(o.ShutdownForce);
        Assert.Equal("Redis", o.ServiceName);
        Assert.False(new InstallOptions().VirtualAccount);
    }

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("90", 90_000)]
    [InlineData("90s", 90_000)]
    [InlineData("1.5s", 1_500)]
    [InlineData("2m", 120_000)]
    [InlineData("1h", 3_600_000)]
    public void Durations(string text, int ms) =>
        Assert.Equal(TimeSpan.FromMilliseconds(ms), CommandLineParser.ParseDuration("--x", text, allowZero: false));

    [Fact]
    public void Uninstall_Options()
    {
        var cmd = Assert.IsType<UninstallCommand>(CommandLineParser.Parse(["uninstall", "--service-name", "MyRedis", "--stop-timeout", "5m"]));
        Assert.Equal("MyRedis", cmd.Options.ServiceName);
        Assert.Equal(TimeSpan.FromMinutes(5), cmd.Options.StopTimeout);
    }

    [Fact]
    public void StoredArguments_RoundTripThroughTheParser()
    {
        var install = new InstallOptions
        {
            ConfigFilePath = @"C:\Program Files\Redis\redis.conf",
            DataDirectory = @"D:\data\",
            Port = 6390,
            LogLevel = "warning",
            LogFile = @"D:\logs\redis.log",
            RestartPolicy = RestartPolicy.Never,
            StopTimeout = TimeSpan.FromSeconds(45),
            HealthInterval = TimeSpan.FromMilliseconds(1500),
            AuthPasswordFile = @"C:\secrets\redis.pw",
            ShutdownForce = true,
            VirtualAccount = true,
        };
        var stored = ServiceArguments.Build(install);
        var parsed = CommandLineParser.ParseRun(stored.ToArray());
        Assert.Equal(install.ConfigFilePath, parsed.ConfigFilePath);
        Assert.Equal(install.DataDirectory, parsed.DataDirectory); // A trailing backslash survives: no quoting involved.
        Assert.Equal(6390, parsed.Port);
        Assert.Equal("warning", parsed.LogLevel);
        Assert.Equal(install.LogFile, parsed.LogFile);
        Assert.Equal(RestartPolicy.Never, parsed.RestartPolicy);
        Assert.Equal(TimeSpan.FromSeconds(45), parsed.StopTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), parsed.HealthInterval);
        Assert.Equal(install.AuthPasswordFile, parsed.AuthPasswordFile);
        Assert.True(parsed.ShutdownForce);
        Assert.DoesNotContain("--virtual-account", stored); // Install-only; the account lives in the SCM config.
    }

    [Fact]
    public void CommandLineFlags_OverrideStoredArguments()
    {
        var layered = CommandLineParser.ParseRun(["--port", "6390", "--loglevel", "notice"]);
        CommandLineParser.ApplyRun(layered, ["--service-name", "X", "--port", "7000"]);
        Assert.Equal(7000, layered.Port);
        Assert.Equal("notice", layered.LogLevel);
        Assert.Equal("X", layered.ServiceName);
    }

    [Fact]
    public void ImagePath_HoldsOnlyTheServiceName() =>
        Assert.Equal(["run", "--service-name", "Redis"], ServiceArguments.ImagePathArguments("Redis"));

    [Fact]
    public void Help_IsEnglishAndListsEveryNewOption()
    {
        var w = new StringWriter();
        CommandLineParser.PrintHelp(w);
        var text = w.ToString();
        foreach (var option in new[] { "--restart-policy", "--stop-timeout", "--virtual-account", "--auth-password-file", "--health-interval", "--shutdown-force" })
            Assert.Contains(option, text);
        Assert.DoesNotMatch(@"[\u4e00-\u9fff]", text);
    }
}
