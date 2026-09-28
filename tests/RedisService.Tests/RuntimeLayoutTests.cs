using RedisService.Service;

namespace RedisService.Tests;

public class RuntimeLayoutTests
{
    [Theory]
    [InlineData(@"C:\Redis\redis.conf", "C:/Redis/redis.conf")]
    [InlineData(@"C:\Redis\data\", "C:/Redis/data")]
    [InlineData(@"C:\", "C:/")]
    [InlineData(@"C:\Program Files\Redis Test\redis.conf", "C:/Program Files/Redis Test/redis.conf")]
    [InlineData(@"\\server\share\redis", "//server/share/redis")]
    [InlineData(@"\\?\C:\Redis\data", "C:/Redis/data")]
    [InlineData(@"\\?\UNC\server\share\x", "//server/share/x")]
    public void ToRedisPath_UsesDriveAndForwardSlashes(string windows, string expected) =>
        Assert.Equal(expected, RuntimeLayout.ToRedisPath(windows));

    [Theory]
    [InlineData(@"C:\Redis", RuntimeFlavor.Msys2, @"C:\")]
    [InlineData(@"C:\bin\Redis", RuntimeFlavor.Msys2, @"C:\")]
    [InlineData(@"C:\a\b\c", RuntimeFlavor.Msys2, @"C:\a")]
    [InlineData(@"C:\msys64\usr\bin", RuntimeFlavor.Msys2, @"C:\msys64")]
    [InlineData(@"C:\cygwin64\bin", RuntimeFlavor.Cygwin, @"C:\cygwin64")]
    [InlineData(@"C:\Redis", RuntimeFlavor.Cygwin, @"C:\")]
    [InlineData(@"\\srv\share\redis\pkg", RuntimeFlavor.Msys2, @"\\srv\share\")]
    public void PosixRoot_MatchesTheRuntime(string install, RuntimeFlavor flavor, string root) =>
        Assert.Equal(root, RuntimeLayout.PosixRoot(install, flavor));

    [Theory]
    // MSYS2: root is two folders up and <root>\bin is shadowed by the /bin -> <root>\usr\bin mount (issues #60, #69).
    [InlineData(@"C:\bin\Redis-8.10.2", RuntimeFlavor.Msys2, @"C:\bin\Redis-8.10.2", true)]
    [InlineData(@"C:\bin\Redis-8.10.2", RuntimeFlavor.Msys2, @"C:\bin\Redis-8.10.2\data", true)]
    [InlineData(@"C:\bin\Redis-8.10.2", RuntimeFlavor.Msys2, @"D:\redis-data", false)]
    [InlineData(@"C:\Redis", RuntimeFlavor.Msys2, @"C:\Redis", false)]
    [InlineData(@"C:\Redis", RuntimeFlavor.Msys2, @"C:\bin\data", true)]
    [InlineData(@"C:\Tools\bin", RuntimeFlavor.Msys2, @"C:\Tools\bin", false)]
    [InlineData(@"C:\Program Files\Redis Test", RuntimeFlavor.Msys2, @"C:\Program Files\Redis Test", false)]
    // Cygwin: root is one folder up; <root>\usr\bin and <root>\usr\lib are the remapped ones.
    [InlineData(@"C:\usr", RuntimeFlavor.Cygwin, @"C:\usr\bin\data", true)]
    [InlineData(@"C:\usr", RuntimeFlavor.Cygwin, @"C:\usr", false)]
    [InlineData(@"C:\bin", RuntimeFlavor.Cygwin, @"C:\bin", false)]
    [InlineData(@"C:\bin\Redis", RuntimeFlavor.Native, @"C:\bin\Redis", false)]
    public void RemappedFolders(string install, RuntimeFlavor flavor, string path, bool trapped) =>
        Assert.Equal(trapped, RuntimeLayout.IsInRemappedFolder(path, install, flavor));

    [Theory]
    [InlineData("/cygdrive/c/Redis/redis.log", @"C:\Redis\redis.log")]
    [InlineData("C:/Redis/redis.log", @"C:\Redis\redis.log")]
    [InlineData("logs/redis.log", @"C:\Redis\logs\redis.log")]
    [InlineData("./x/../redis.log", @"C:\Redis\redis.log")]
    [InlineData("/Redis/redis.log", @"C:\Redis\redis.log")]
    [InlineData("/bin/x.log", @"C:\usr\bin\x.log")]
    public void ConfPathToWindows_Msys2(string confPath, string expected) =>
        Assert.Equal(expected, RuntimeLayout.ConfPathToWindows(confPath, @"C:\Redis", @"C:\Redis", RuntimeFlavor.Msys2));

    [Fact]
    public void ConfPathToWindows_CygwinUsrBin() =>
        Assert.Equal(@"C:\cyg\bin\x", RuntimeLayout.ConfPathToWindows("/usr/bin/x", @"C:\cyg\pkg", @"C:\cyg\pkg", RuntimeFlavor.Cygwin));

    [Theory]
    [InlineData("C:/x", true)]
    [InlineData(@"C:\x", true)]
    [InlineData(@"\\srv\s", true)]
    [InlineData("x/y", false)]
    [InlineData("/x", false)]
    public void IsWindowsAbsolute(string path, bool expected) => Assert.Equal(expected, RuntimeLayout.IsWindowsAbsolute(path));
}
