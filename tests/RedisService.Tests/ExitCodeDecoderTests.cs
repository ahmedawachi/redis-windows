using RedisService.Service;

namespace RedisService.Tests;

public class ExitCodeDecoderTests
{
    [Fact]
    public void Zero_IsClean() => Assert.True(ExitCodeDecoder.Decode(0, RuntimeFlavor.Msys2).IsClean);

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public void SmallCodes_AreExitStatus(int code)
    {
        var s = ExitCodeDecoder.Decode(code, RuntimeFlavor.Msys2);
        Assert.Equal(ExitKind.Exit, s.Kind);
        Assert.Equal(code, s.Value);
        Assert.Equal(code, s.ShellExitCode);
    }

    [Theory]
    [InlineData(0x600, 6, false)]   // serverPanic -> abort(), e.g. a failed allocation
    [InlineData(0x8600, 6, true)]   // abort with the core flag
    [InlineData(0xB00, 11, false)]  // SIGSEGV
    [InlineData(0x900, 9, false)]   // SIGKILL
    [InlineData(0xF00, 15, false)]  // SIGTERM
    public void MultiplesOf256_AreSignals(int code, int signal, bool core)
    {
        var s = ExitCodeDecoder.Decode(code, RuntimeFlavor.Cygwin);
        Assert.Equal(ExitKind.Signal, s.Kind);
        Assert.Equal(signal, s.Value);
        Assert.Equal(core, s.CoreDumped);
        Assert.Equal(128 + signal, s.ShellExitCode);
        Assert.Contains(ExitCodeDecoder.SignalName(signal), s.Describe());
    }

    [Theory]
    [InlineData(unchecked((int)0xC0000005), "STATUS_ACCESS_VIOLATION")]
    [InlineData(-1, "terminated")]
    [InlineData(1067, "")]
    [InlineData(0x601, "")]
    public void OtherCodes_AreWindowsStatuses(int code, string name)
    {
        var s = ExitCodeDecoder.Decode(code, RuntimeFlavor.Msys2);
        Assert.Equal(ExitKind.WindowsStatus, s.Kind);
        Assert.Contains(name, s.Describe());
        Assert.Equal(1, s.ShellExitCode);
    }

    [Theory]
    [InlineData(137, ExitKind.Signal, 9)]
    [InlineData(134, ExitKind.Signal, 6)]
    [InlineData(1, ExitKind.Exit, 1)]
    public void NativeUnix_Uses128PlusSignal(int code, ExitKind kind, int value)
    {
        var s = ExitCodeDecoder.Decode(code, RuntimeFlavor.Native);
        Assert.Equal(kind, s.Kind);
        Assert.Equal(value, s.Value);
    }
}
