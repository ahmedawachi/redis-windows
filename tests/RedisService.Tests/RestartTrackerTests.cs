using RedisService.CommandLine;
using RedisService.Service;

namespace RedisService.Tests;

public class RestartTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 16, 38, 41, TimeSpan.Zero);
    private static readonly ExitStatus Abort = ExitCodeDecoder.Decode(0x600, RuntimeFlavor.Msys2);
    private static readonly ExitStatus Clean = ExitCodeDecoder.Decode(0, RuntimeFlavor.Msys2);

    private static RestartTracker Tracker(RestartPolicy policy = RestartPolicy.OnCrash, double jitter = 0, int max = 5, int windowMinutes = 10) =>
        new(policy, max, TimeSpan.FromMinutes(windowMinutes), () => jitter);

    [Fact]
    public void Backoff_DoublesFromOneSecond_CappedAt60()
    {
        var t = Tracker(max: 100, windowMinutes: 1000);
        var delays = new List<double>();
        for (var i = 0; i < 9; i++)
        {
            var d = Assert.IsType<SupervisorDecision.Restart>(t.OnUnexpectedEnd(RunEnd.Exited, Abort, TimeSpan.FromSeconds(1), T0.AddMinutes(i)));
            delays.Add(d.Delay.TotalSeconds);
        }
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60, 60], delays);
    }

    [Fact]
    public void Jitter_AddsAtMostTwentyPercent()
    {
        var d = Assert.IsType<SupervisorDecision.Restart>(Tracker(jitter: 1).OnUnexpectedEnd(RunEnd.Exited, Abort, TimeSpan.Zero, T0));
        Assert.Equal(1.2, d.Delay.TotalSeconds, 3);
    }

    [Fact]
    public void Backoff_ResetsAfterFiveHealthyMinutes()
    {
        var t = Tracker(max: 100);
        t.OnUnexpectedEnd(RunEnd.Exited, Abort, TimeSpan.FromSeconds(3), T0);
        t.OnUnexpectedEnd(RunEnd.Exited, Abort, TimeSpan.FromSeconds(3), T0.AddSeconds(10));
        var third = Assert.IsType<SupervisorDecision.Restart>(t.OnUnexpectedEnd(RunEnd.Exited, Abort, TimeSpan.FromMinutes(5), T0.AddMinutes(6)));
        Assert.Equal(1, third.Delay.TotalSeconds);
    }

    [Fact]
    public void FiveCrashesInTenMinutes_GiveUpWith1067()
    {
        var t = Tracker();
        for (var i = 0; i < 4; i++)
            Assert.IsType<SupervisorDecision.Restart>(t.OnUnexpectedEnd(RunEnd.Exited, Abort, TimeSpan.FromSeconds(10), T0.AddMinutes(i)));
        var stop = Assert.IsType<SupervisorDecision.Stop>(t.OnUnexpectedEnd(RunEnd.Exited, Abort, TimeSpan.FromSeconds(10), T0.AddMinutes(4)));
        Assert.Equal(1067, stop.ServiceExitCode);
    }

    [Fact]
    public void CrashesOutsideTheWindow_DoNotCount()
    {
        var t = Tracker();
        for (var i = 0; i < 12; i++)
            Assert.IsType<SupervisorDecision.Restart>(t.OnUnexpectedEnd(RunEnd.Exited, Abort, TimeSpan.FromMinutes(6), T0.AddMinutes(i * 3)));
    }

    [Fact]
    public void CleanUnrequestedExit_StopsTheServiceByDefault()
    {
        var stop = Assert.IsType<SupervisorDecision.Stop>(Tracker().OnUnexpectedEnd(RunEnd.Exited, Clean, TimeSpan.FromHours(1), T0));
        Assert.Equal(0, stop.ServiceExitCode);
        Assert.Contains("SHUTDOWN", stop.Reason);
    }

    [Fact]
    public void CleanExit_RestartsUnderPolicyAlways() =>
        Assert.IsType<SupervisorDecision.Restart>(Tracker(RestartPolicy.Always).OnUnexpectedEnd(RunEnd.Exited, Clean, TimeSpan.FromHours(1), T0));

    [Fact]
    public void PolicyNever_CrashStopsWith1067() =>
        Assert.Equal(1067, Assert.IsType<SupervisorDecision.Stop>(Tracker(RestartPolicy.Never).OnUnexpectedEnd(RunEnd.Exited, Abort, TimeSpan.Zero, T0)).ServiceExitCode);

    [Fact]
    public void PolicyNever_CleanExitStopsWithZero() =>
        Assert.Equal(0, Assert.IsType<SupervisorDecision.Stop>(Tracker(RestartPolicy.Never).OnUnexpectedEnd(RunEnd.Exited, Clean, TimeSpan.Zero, T0)).ServiceExitCode);

    [Theory]
    [InlineData(RunEnd.Hung)]
    [InlineData(RunEnd.StartTimeout)]
    [InlineData(RunEnd.LaunchFailed)]
    public void HangsAndStartFailures_CountAsCrashes(RunEnd end)
    {
        var t = Tracker(max: 2);
        Assert.IsType<SupervisorDecision.Restart>(t.OnUnexpectedEnd(end, end == RunEnd.LaunchFailed ? null : Clean, TimeSpan.Zero, T0));
        Assert.IsType<SupervisorDecision.Stop>(t.OnUnexpectedEnd(end, null, TimeSpan.Zero, T0.AddSeconds(5)));
    }
}
