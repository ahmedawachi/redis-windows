using RedisService.Service;

namespace RedisService.Tests;

/// <summary>The RESP client against a real redis-server: AUTH, PING, SHUTDOWN and the error paths.</summary>
[Collection("real-redis")]
public class RealRedisControlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ping_WithPassword_IsPong()
    {
        await using var redis = RedisTestInstance.Create();
        await redis.StartDirectAsync();
        var probe = await new RedisControl(redis.Endpoint()).PingAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(ProbeStatus.Ready, probe.Status);
    }

    [Fact]
    public async Task WrongPassword_IsRejectedByName_ButCountsAsAlive()
    {
        await using var redis = RedisTestInstance.Create();
        await redis.StartDirectAsync();

        var ex = await Assert.ThrowsAsync<RespAuthenticationException>(() =>
            RespConnection.ConnectAsync(redis.Endpoint("wrong"), TimeSpan.FromSeconds(2), Ct));
        Assert.StartsWith("WRONGPASS", ex.ServerMessage);

        var probe = await new RedisControl(redis.Endpoint("wrong")).PingAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(ProbeStatus.Alive, probe.Status);
        Assert.Equal(ProbeIssue.Credentials, probe.Issue);
        Assert.Contains("WRONGPASS", probe.Detail);
    }

    [Fact]
    public async Task NoPassword_GetsNoauth_WhichIsAlive()
    {
        await using var redis = RedisTestInstance.Create();
        await redis.StartDirectAsync();
        var probe = await new RedisControl(redis.Endpoint(password: null)).PingAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(ProbeStatus.Alive, probe.Status);
        Assert.Equal(ProbeIssue.Credentials, probe.Issue);
        Assert.Contains("NOAUTH", probe.Detail);

        // Without credentials SHUTDOWN is refused, FORCE is not attempted, and the server keeps running.
        var shutdown = await new RedisControl(redis.Endpoint(password: null)).ShutdownAsync(forceOnError: true, TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ShutdownOutcome.Refused, shutdown.Outcome);
        Assert.True(shutdown.IsCredentialsProblem);
        Assert.Contains("NOAUTH", shutdown.Detail);
        await redis.WaitForPongAsync();
    }

    [Fact]
    public async Task AclUser_Authenticates()
    {
        await using var redis = RedisTestInstance.Create("user svc on >svcpass ~* +@all");
        await redis.StartDirectAsync();
        var probe = await new RedisControl(redis.Endpoint("svcpass", "svc")).PingAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(ProbeStatus.Ready, probe.Status);
    }

    [Fact]
    public async Task Shutdown_SavesAndExits()
    {
        await using var redis = RedisTestInstance.Create("save 3600 1");
        var p = await redis.StartDirectAsync();
        Assert.Equal("OK", (await redis.CommandAsync("SET", "k", "v")).Text);

        var result = await new RedisControl(redis.Endpoint()).ShutdownAsync(false, TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(ShutdownOutcome.Accepted, result.Outcome);
        await p.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("User requested shutdown", redis.Log);
        Assert.Contains("DB saved on disk", redis.Log);
        Assert.True(File.Exists(Path.Combine(redis.DataDir, "dump.rdb")));
    }

    [Fact]
    public async Task Shutdown_DuringBgsave_ExitsCleanly_WithoutTempFile()
    {
        await using var redis = RedisTestInstance.Create("save 3600 1\nrdbcompression no");
        var p = await redis.StartDirectAsync();
        Assert.Equal("OK", (await redis.CommandAsync("DEBUG", "POPULATE", "300000", "key", "256")).Text);
        await redis.CommandAsync("BGSAVE");

        var result = await new RedisControl(redis.Endpoint()).ShutdownAsync(false, TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(ShutdownOutcome.Accepted, result.Outcome);
        await p.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("DB saved on disk", redis.Log);
        Assert.Empty(Directory.GetFiles(redis.DataDir, "temp-*.rdb"));
    }

    [Fact]
    public async Task Shutdown_WhenTheSaveFails_IsRefused_AndForceIsOptIn()
    {
        Assert.SkipWhen(Environment.UserName == "root", "root ignores the read-only directory");
        await using var redis = RedisTestInstance.Create("save 3600 1");
        var p = await redis.StartDirectAsync();
        await redis.CommandAsync("SET", "k", "v");
        File.SetUnixFileMode(redis.DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var refused = await new RedisControl(redis.Endpoint()).ShutdownAsync(false, TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(ShutdownOutcome.Refused, refused.Outcome);
            Assert.True(refused.IsSaveFailure);
            Assert.Contains("Errors trying to SHUTDOWN", refused.Detail);
            await redis.WaitForPongAsync(); // Still running: nothing was lost yet.

            var forced = await new RedisControl(redis.Endpoint()).ShutdownAsync(true, TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(ShutdownOutcome.AcceptedWithForce, forced.Outcome);
            await p.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        finally
        {
            File.SetUnixFileMode(redis.DataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Shutdown_RenamedCommand_IsRefused_AndForceIsNotSent()
    {
        await using var redis = RedisTestInstance.Create("rename-command SHUTDOWN \"\"");
        await redis.StartDirectAsync();
        var result = await new RedisControl(redis.Endpoint()).ShutdownAsync(forceOnError: true, TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ShutdownOutcome.Refused, result.Outcome);
        Assert.Equal("ERR", result.ErrorCode);
        Assert.False(result.IsSaveFailure);
        Assert.DoesNotContain("FORCE", result.Detail);
        await redis.WaitForPongAsync();
    }

    [Fact]
    public async Task Shutdown_WrongPassword_IsUnreachable_AndServerSurvives()
    {
        await using var redis = RedisTestInstance.Create();
        await redis.StartDirectAsync();
        var result = await new RedisControl(redis.Endpoint("nope")).ShutdownAsync(false, TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ShutdownOutcome.Unreachable, result.Outcome);
        Assert.Contains("WRONGPASS", result.Detail);
        await redis.WaitForPongAsync();
    }

    [Fact]
    public async Task NothingListening_IsUnresponsiveAndUnreachable()
    {
        var endpoint = new RedisEndpoint("127.0.0.1", RedisTestInstance.FreePort(), null, null, null);
        var probe = await new RedisControl(endpoint).PingAsync(TimeSpan.FromSeconds(1), Ct);
        Assert.Equal(ProbeStatus.Unresponsive, probe.Status);
        var shutdown = await new RedisControl(endpoint).ShutdownAsync(false, TimeSpan.FromSeconds(1), Ct);
        Assert.Equal(ShutdownOutcome.Unreachable, shutdown.Outcome);
    }

    [Fact]
    public async Task BusyServer_IsUnresponsive_UntilItWakesUp()
    {
        await using var redis = RedisTestInstance.Create();
        await redis.StartDirectAsync();
        await redis.FireAsync("DEBUG", "SLEEP", "2");
        await Task.Delay(200, Ct);
        var probe = await new RedisControl(redis.Endpoint()).PingAsync(TimeSpan.FromMilliseconds(500), Ct);
        Assert.Equal(ProbeStatus.Unresponsive, probe.Status);
        await redis.WaitForPongAsync(TimeSpan.FromSeconds(10));
    }
}
