using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RedisService.CommandLine;
using RedisService.Service;

namespace RedisService.Tests;

/// <summary>The supervisor driving a real redis-server: readiness, restart, crash loop, hang detection, graceful stop.</summary>
[Collection("real-redis")]
public class SupervisorIntegrationTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public readonly ConcurrentQueue<LogEntry> Log = new();
        public RedisSupervisor Supervisor { get; }
        public Task<SupervisorResult> Run { get; }

        public Harness(RedisTestInstance redis, Action<RunOptions>? configure = null, string? redisServerPath = null)
        {
            var options = new RunOptions
            {
                ConfigFilePath = redis.ConfPath,
                RedisServerPath = redisServerPath ?? RedisTestInstance.ServerPath,
                StartTimeout = TimeSpan.FromSeconds(20),
                StopTimeout = TimeSpan.FromSeconds(30),
                HealthInterval = TimeSpan.FromMilliseconds(500),
                HealthFailures = 6,
            };
            configure?.Invoke(options);
            var settings = SupervisorSettingsResolver.Resolve(options, redis.Dir.Path, redis.Dir.Path);
            Supervisor = new RedisSupervisor(settings, new ManagedProcessLauncher(),
                new ListLogger("RedisService", Log), new ListLogger("RedisService.RedisOutput", Log), jitter: () => 0);
            Run = Task.Run(() => Supervisor.RunAsync(_stop.Token));
        }

        public IEnumerable<LogEntry> Events(EventId id) => Log.Where(e => e.EventId.Id == id.Id);

        public async Task<int> WaitReadyAsync(long count = 1)
        {
            Assert.True(await Wait.UntilAsync(() => Supervisor.ReadyCount >= count || Run.IsCompleted, TimeSpan.FromSeconds(30)),
                "redis-server did not become ready: " + string.Join(" | ", Log.Select(e => e.Message)));
            Assert.False(Run.IsCompleted, "the supervisor ended early: " + string.Join(" | ", Log.Select(e => e.Message)));
            return Supervisor.CurrentProcessId!.Value;
        }

        public async Task<SupervisorResult> StopAsync()
        {
            await _stop.CancelAsync();
            return await Run.WaitAsync(TimeSpan.FromSeconds(60));
        }

        public async ValueTask DisposeAsync()
        {
            if (!Run.IsCompleted)
            {
                await _stop.CancelAsync();
                try { await Run.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (TimeoutException) { Supervisor.ForceStop(); }
            }
        }
    }

    private static void KillHard(int pid) => Process.GetProcessById(pid).Kill();

    [Fact]
    public async Task Start_Ready_GracefulStop_SavesAndDataSurvives()
    {
        await using var redis = RedisTestInstance.Create("save 3600 1");
        await using (var h = new Harness(redis))
        {
            await h.WaitReadyAsync();
            Assert.Single(h.Events(EventIds.Ready));
            Assert.Equal("OK", (await redis.CommandAsync("SET", "survivor", "yes")).Text);

            var result = await h.StopAsync();
            Assert.True(result.StopRequested);
            Assert.Equal(0, result.ForegroundExitCode);
            Assert.True(result.LastExit!.IsClean, result.LastExit.Describe() + " :: " + string.Join(" | ", h.Log.Select(e => e.Message)));
            Assert.Contains("User requested shutdown", redis.Log);
            Assert.Contains("DB saved on disk", redis.Log);
            Assert.Single(h.Events(EventIds.Stopping));
            Assert.Single(h.Events(EventIds.Stopped));
            Assert.Empty(h.Events(EventIds.Crashed));
        }

        await using var again = new Harness(redis);
        await again.WaitReadyAsync();
        Assert.Equal("yes", (await redis.CommandAsync("GET", "survivor")).Text);
    }

    /// <summary>W-2: a password set only by an ACL 'user' line answers PING with NOAUTH, which is not "still loading".</summary>
    [Fact]
    public async Task AclOnlyPassword_IsReadyWithAWarning_AndStopDoesNotSigkill()
    {
        await using var redis = RedisTestInstance.Create("save 3600 1\nuser default on >aclpass ~* &* +@all", withPassword: false);
        await using var h = new Harness(redis);
        var sw = Stopwatch.StartNew();
        await h.WaitReadyAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"readiness took {sw.Elapsed} (it waited for the start timeout)");
        var warnings = h.Events(EventIds.ConfigWarning).Select(e => e.Message).ToList();
        Assert.Contains(warnings, w => w.Contains("ACL users"));
        Assert.Contains(warnings, w => w.Contains("cannot authenticate"));

        await using (var c = await RespConnection.ConnectAsync(redis.Endpoint("aclpass"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            Assert.Equal("OK", (await c.ExecuteAsync(["SET", "acl", "kept"], TestContext.Current.CancellationToken)).Text);

        var result = await h.StopAsync();
        Assert.True(result.StopRequested);
        Assert.Contains(h.Events(EventIds.StopProblem), e => e.Message.Contains("not authenticated"));
        // The fallback asked the OS to stop it (SIGTERM here), so the final save still happened.
        Assert.Contains("DB saved on disk", redis.Log);
        Assert.NotEqual(ExitKind.Signal, result.LastExit!.Kind);
    }

    [Fact]
    public async Task KilledRedis_IsRestarted_WithOneErrorEvent()
    {
        await using var redis = RedisTestInstance.Create();
        await using var h = new Harness(redis);
        var pid1 = await h.WaitReadyAsync();

        var sw = Stopwatch.StartNew();
        KillHard(pid1);
        var pid2 = await h.WaitReadyAsync(2);
        Assert.NotEqual(pid1, pid2);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"restart took {sw.Elapsed}");

        var crash = Assert.Single(h.Events(EventIds.Crashed));
        Assert.Equal(LogLevel.Error, crash.Level);
        Assert.Contains("signal 9", crash.Message);
        Assert.Single(h.Events(EventIds.Restarting));
        await redis.WaitForPongAsync();
    }

    [Fact]
    public async Task CrashLoop_GivesUpWith1067()
    {
        await using var redis = RedisTestInstance.Create();
        await using var h = new Harness(redis, o => o.MaxRestarts = 3);
        for (var i = 1; i <= 3; i++)
        {
            var pid = await h.WaitReadyAsync(i);
            KillHard(pid);
        }
        var result = await h.Run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(result.GaveUp);
        Assert.Equal(1067, result.ExitCode);
        Assert.Equal(1067, result.ForegroundExitCode);
        Assert.Equal(3, h.Events(EventIds.Crashed).Count());
        Assert.Single(h.Events(EventIds.GaveUp));
        Assert.Null(h.Supervisor.CurrentProcessId);
    }

    [Fact]
    public async Task ClientShutdown_StopsSupervision_UnderTheDefaultPolicy()
    {
        await using var redis = RedisTestInstance.Create();
        await using var h = new Harness(redis);
        await h.WaitReadyAsync();
        await redis.FireAsync("SHUTDOWN");
        var result = await h.Run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(result.StopRequested);
        Assert.False(result.GaveUp);
        Assert.Equal(0, result.ExitCode);
        Assert.Single(h.Events(EventIds.UnrequestedExit));
        Assert.Empty(h.Events(EventIds.Crashed));
    }

    [Fact]
    public async Task ClientShutdown_IsRestarted_UnderPolicyAlways()
    {
        await using var redis = RedisTestInstance.Create();
        await using var h = new Harness(redis, o => o.RestartPolicy = RestartPolicy.Always);
        var pid1 = await h.WaitReadyAsync();
        await redis.FireAsync("SHUTDOWN");
        var pid2 = await h.WaitReadyAsync(2);
        Assert.NotEqual(pid1, pid2);
    }

    [Fact]
    public async Task PolicyNever_Crash_Ends1067_WithDecodedForegroundCode()
    {
        await using var redis = RedisTestInstance.Create();
        await using var h = new Harness(redis, o => o.RestartPolicy = RestartPolicy.Never);
        KillHard(await h.WaitReadyAsync());
        var result = await h.Run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1067, result.ExitCode);
        Assert.True(result.GaveUp);
        Assert.Equal(ExitKind.Signal, result.LastExit!.Kind);
    }

    [Fact]
    public async Task Hang_IsDetected_AndRestarted()
    {
        await using var redis = RedisTestInstance.Create();
        await using var h = new Harness(redis, o => { o.HealthInterval = TimeSpan.FromMilliseconds(500); o.HealthFailures = 4; });
        var pid1 = await h.WaitReadyAsync();
        await redis.FireAsync("DEBUG", "SLEEP", "30");
        var pid2 = await h.WaitReadyAsync(2);
        Assert.NotEqual(pid1, pid2);
        var crash = Assert.Single(h.Events(EventIds.Crashed));
        Assert.Contains("stopped answering PING", crash.Message);
    }

    [Fact]
    public async Task ShortPause_IsNotAHang()
    {
        await using var redis = RedisTestInstance.Create();
        await using var h = new Harness(redis, o => { o.HealthInterval = TimeSpan.FromMilliseconds(500); o.HealthFailures = 6; });
        var pid = await h.WaitReadyAsync();
        await redis.FireAsync("DEBUG", "SLEEP", "1");
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal(1, h.Supervisor.ReadyCount);
        Assert.Equal(pid, h.Supervisor.CurrentProcessId);
        Assert.Empty(h.Events(EventIds.Crashed));
    }

    [Fact]
    public async Task Segfault_CrashEventCarriesTheBugReport()
    {
        await using var redis = RedisTestInstance.Create();
        await using var h = new Harness(redis);
        await h.WaitReadyAsync();
        await redis.FireAsync("DEBUG", "SEGFAULT");
        await h.WaitReadyAsync(2);
        var crash = Assert.Single(h.Events(EventIds.Crashed));
        Assert.Contains("REDIS BUG REPORT START", crash.Message);
        Assert.Contains(redis.LogPath, crash.Message);
    }

    [Fact]
    public async Task BadConfig_ExitsWithStatus1_ExcerptFromStderr_ThenGivesUp()
    {
        await using var redis = RedisTestInstance.Create("this-is-not-a-directive yes");
        await using var h = new Harness(redis, o => o.MaxRestarts = 2);
        var result = await h.Run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(result.GaveUp);
        var crash = h.Events(EventIds.Crashed).First();
        Assert.Contains("exited with status 1", crash.Message);
        Assert.Contains("this-is-not-a-directive", crash.Message);
    }

    [Fact]
    public async Task ProcessThatNeverAnswers_HitsTheStartTimeout()
    {
        await using var redis = RedisTestInstance.Create();
        var fake = redis.Dir.File("fake-redis-server", "#!/bin/sh\nexec sleep 60\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var h = new Harness(redis, o => { o.StartTimeout = TimeSpan.FromSeconds(2); o.MaxRestarts = 2; }, fake);
        var result = await h.Run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(result.GaveUp);
        Assert.Contains(h.Log, e => e.Message.Contains("did not answer PING"));
    }

    [Fact]
    public async Task StopDuringRestartBackoff_EndsCleanly()
    {
        await using var redis = RedisTestInstance.Create();
        await using var h = new Harness(redis);
        KillHard(await h.WaitReadyAsync());
        Assert.True(await Wait.UntilAsync(() => h.Events(EventIds.Restarting).Any(), TimeSpan.FromSeconds(10)));
        var result = await h.StopAsync();
        Assert.True(result.StopRequested);
    }

    [Fact]
    public async Task ForceStop_KillsImmediately()
    {
        await using var redis = RedisTestInstance.Create();
        await using var h = new Harness(redis);
        var pid = await h.WaitReadyAsync();
        await redis.FireAsync("DEBUG", "SLEEP", "30"); // SHUTDOWN cannot be served now.
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var stopping = h.StopAsync();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        h.Supervisor.ForceStop();
        var result = await stopping;
        Assert.True(result.StopRequested);
        Assert.True(await Wait.UntilAsync(() => ProcessGone(pid), TimeSpan.FromSeconds(5)));
    }

    private static bool ProcessGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
