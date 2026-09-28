using RedisService.Service;

namespace RedisService.Tests;

public class ConfFileTests
{
    private static ConfPathContext Native(string cwd) => new(cwd, cwd, RuntimeFlavor.Native);

    [Theory]
    [InlineData("port 6379", new[] { "port", "6379" })]
    [InlineData("  save 900 1   300 10 ", new[] { "save", "900", "1", "300", "10" })]
    [InlineData("requirepass \"p a\\\"ss\"", new[] { "requirepass", "p a\"ss" })]
    [InlineData("requirepass 'it\\'s'", new[] { "requirepass", "it's" })]
    [InlineData("logfile \"\"", new[] { "logfile", "" })]
    [InlineData("x \"\\x41\\n\"", new[] { "x", "A\n" })]
    [InlineData("dir \"C:\\\\Redis Data\"", new[] { "dir", "C:\\Redis Data" })]
    public void SplitArgs_FollowsSdssplitargs(string line, string[] expected) =>
        Assert.Equal(expected, RedisConfFile.SplitArgs(line));

    [Theory]
    [InlineData("requirepass \"unterminated")]
    [InlineData("requirepass \"closed\"trailing")]
    [InlineData("requirepass 'x")]
    public void SplitArgs_RejectsUnbalancedQuotes(string line) => Assert.Null(RedisConfFile.SplitArgs(line));

    [Fact]
    public void LastValueWins_NamesAreCaseInsensitive_CommentsIgnored()
    {
        var conf = RedisConfFile.Parse("# port 1\nport 6379\nPORT 6380\n\nrequirepass first\nrequirepass second\n", Native("/tmp"));
        Assert.Equal("6380", conf.Get("port"));
        Assert.Equal("second", conf.Get("requirepass"));
        Assert.Null(conf.Get("tls-port"));
    }

    [Fact]
    public void Overrides_AreAppliedLast()
    {
        var conf = RedisConfFile.Parse("port 6379\ndir /data\n", Native("/tmp"), [new("port", "7000"), new("dir", "/other")]);
        Assert.Equal("7000", conf.Get("port"));
        Assert.Equal("/other", conf.EffectiveDirectory);
    }

    [Fact]
    public void Include_RelativeToCurrentDirectory_AndDirChangesIt()
    {
        using var tmp = new TempDir();
        tmp.File("a.conf", "port 7001\n");
        tmp.File("sub/b.conf", "requirepass fromsub\n");
        var main = tmp.File("main.conf", $"include a.conf\ndir {Path.Combine(tmp.Path, "sub")}\ninclude b.conf\nport 7002\n");
        var conf = RedisConfFile.Load(main, Native(tmp.Path));
        Assert.Equal("7002", conf.Get("port"));
        Assert.Equal("fromsub", conf.Get("requirepass"));
        Assert.Empty(conf.Warnings);
        Assert.Equal(Path.Combine(tmp.Path, "sub"), conf.EffectiveDirectory);
    }

    [Fact]
    public void Include_Glob_InOrdinalOrder()
    {
        using var tmp = new TempDir();
        tmp.File("conf.d/20.conf", "port 7020\n");
        tmp.File("conf.d/10.conf", "port 7010\nrequirepass ten\n");
        var main = tmp.File("main.conf", $"include {tmp.Path}/conf.d/*.conf\n");
        var conf = RedisConfFile.Load(main, Native(tmp.Path));
        Assert.Equal("7020", conf.Get("port"));
        Assert.Equal("ten", conf.Get("requirepass"));
    }

    [Fact]
    public void MissingInclude_IsAWarningNotAFailure()
    {
        using var tmp = new TempDir();
        var main = tmp.File("main.conf", "include nope.conf\nport 1234\n");
        var conf = RedisConfFile.Load(main, Native(tmp.Path));
        Assert.Equal("1234", conf.Get("port"));
        Assert.Single(conf.Warnings);
    }

    [Fact]
    public void IncludeLoop_IsCut()
    {
        using var tmp = new TempDir();
        var main = tmp.File("main.conf", "include main.conf\n");
        var conf = RedisConfFile.Load(main, Native(tmp.Path));
        Assert.Contains(conf.Warnings, w => w.Contains("nesting"));
    }

    [Fact]
    public void WindowsFlavor_ResolvesCygdriveAndRelativePaths()
    {
        var ctx = new ConfPathContext(@"C:\Redis", @"C:\Redis", RuntimeFlavor.Msys2);
        var conf = RedisConfFile.Parse("dir ./data\nlogfile redis.log\n", ctx);
        Assert.Equal(@"C:\Redis\data", conf.EffectiveDirectory);
        Assert.True(conf.DirectoryWasRelative);
        conf = RedisConfFile.Parse("dir /cygdrive/d/redis\n", ctx);
        Assert.Equal(@"D:\redis", conf.EffectiveDirectory);
        conf = RedisConfFile.Parse("dir \"C:/Program Files/Redis/data\"\n", ctx);
        Assert.Equal(@"C:\Program Files\Redis\data", conf.EffectiveDirectory);
    }
}
