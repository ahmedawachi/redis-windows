using RedisService.CommandLine;

namespace RedisService.Service;

/// <summary>Why a run of redis-server ended.</summary>
public enum RunEnd
{
    /// <summary>The process exited on its own.</summary>
    Exited,

    /// <summary>The liveness probe failed repeatedly and the wrapper killed it.</summary>
    Hung,

    /// <summary>It never answered PING within the start timeout and was killed.</summary>
    StartTimeout,

    /// <summary>The process could not be launched at all.</summary>
    LaunchFailed,
}

public abstract record SupervisorDecision
{
    public sealed record Restart(TimeSpan Delay, int CrashesInWindow) : SupervisorDecision;

    /// <summary>Stop supervising and end the service with this Windows exit code (0 = not a failure).</summary>
    public sealed record Stop(int ServiceExitCode, string Reason) : SupervisorDecision;
}

/// <summary>
/// Restart policy with exponential backoff and a crash-loop cap. Pure: every method takes the current time,
/// so it is deterministic under test.
/// Backoff is min(1 s * 2^n, 60 s) plus up to 20 % jitter, where n counts consecutive failures and resets once a run
/// stayed up for <see cref="HealthyResetAfter"/>. <see cref="MaxCrashes"/> failures within <see cref="Window"/>
/// give up with exit code 1067 (ERROR_PROCESS_ABORTED) so the SCM failure actions take over.
/// </summary>
public sealed class RestartTracker(RestartPolicy policy, int maxCrashes, TimeSpan window, Func<double>? jitterSource = null)
{
    public const int CrashLoopExitCode = 1067;
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HealthyResetAfter = TimeSpan.FromMinutes(5);

    private readonly Queue<DateTimeOffset> _failures = new();
    private readonly Func<double> _jitter = jitterSource ?? Random.Shared.NextDouble;
    private int _consecutive;

    public RestartPolicy Policy { get; } = policy;
    public int MaxCrashes { get; } = maxCrashes;
    public TimeSpan Window { get; } = window;

    /// <summary>Consecutive failures since the last healthy run.</summary>
    public int ConsecutiveFailures => _consecutive;

    /// <summary>Decides what to do after a run that the wrapper did not ask to end.</summary>
    public SupervisorDecision OnUnexpectedEnd(RunEnd end, ExitStatus? exit, TimeSpan uptime, DateTimeOffset now)
    {
        var cleanExit = end == RunEnd.Exited && exit is { IsClean: true };

        if (cleanExit && Policy != RestartPolicy.Always)
        {
            return new SupervisorDecision.Stop(0,
                $"redis-server exited cleanly without a stop request from the service (for example a client sent SHUTDOWN); restart policy is {CommandLineParser.FormatRestartPolicy(Policy)}, so the service stops.");
        }

        if (!cleanExit && Policy == RestartPolicy.Never)
            return new SupervisorDecision.Stop(CrashLoopExitCode, "Restart policy is never; the service stops with exit code 1067 so SCM recovery actions can run.");

        if (uptime >= HealthyResetAfter) _consecutive = 0;

        _failures.Enqueue(now);
        while (_failures.Count > 0 && now - _failures.Peek() > Window) _failures.Dequeue();

        if (_failures.Count >= MaxCrashes)
        {
            return new SupervisorDecision.Stop(CrashLoopExitCode,
                $"redis-server failed {_failures.Count} times within {FormatSpan(Window)}; giving up. The service stops with exit code 1067 so SCM recovery actions can run.");
        }

        var exponent = Math.Min(_consecutive, 16);
        _consecutive++;
        var baseDelay = TimeSpan.FromTicks(Math.Min(BaseDelay.Ticks << exponent, MaxDelay.Ticks));
        var jitter = TimeSpan.FromTicks((long)(baseDelay.Ticks * 0.2 * Math.Clamp(_jitter(), 0, 1)));
        return new SupervisorDecision.Restart(baseDelay + jitter, _failures.Count);
    }

    public static string FormatSpan(TimeSpan t) =>
        t.TotalMinutes >= 1 && t.Seconds == 0 ? $"{t.TotalMinutes:0.#} min" : $"{t.TotalSeconds:0.#} s";
}
