using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RedisService.Service;

/// <summary>Event Log event ids for the lifecycle events (stable, so monitoring can alert on them).</summary>
public static class EventIds
{
    public static readonly EventId Starting = new(1000, "Starting");
    public static readonly EventId Ready = new(1001, "Ready");
    public static readonly EventId Crashed = new(1002, "Crashed");
    public static readonly EventId Restarting = new(1003, "Restarting");
    public static readonly EventId GaveUp = new(1004, "GaveUp");
    public static readonly EventId Stopping = new(1005, "Stopping");
    public static readonly EventId Stopped = new(1006, "Stopped");
    public static readonly EventId UnrequestedExit = new(1007, "UnrequestedExit");
    public static readonly EventId StopProblem = new(1008, "StopProblem");
    public static readonly EventId ConfigWarning = new(1009, "ConfigWarning");
    public static readonly EventId StartFailed = new(1010, "StartFailed");
    public static readonly EventId RedisStdout = new(2000, "RedisStdout");
    public static readonly EventId RedisStderr = new(2001, "RedisStderr");
}

public sealed record SupervisorResult(int ExitCode, bool StopRequested, bool GaveUp, ExitStatus? LastExit, string Reason)
{
    /// <summary>Exit code for the foreground command: 0 after a requested stop, 1067 after giving up, else the decoded code.</summary>
    public int ForegroundExitCode => StopRequested ? 0 : GaveUp ? RestartTracker.CrashLoopExitCode : LastExit?.ShellExitCode ?? ExitCode;
}

/// <summary>
/// Supervises redis-server: start, wait for PONG, watch for exit / hang / stop request, restart with backoff,
/// and give up with exit code 1067 after a crash loop so the Service Control Manager's recovery actions take over.
/// Host-agnostic: the Windows service and the foreground console mode both drive it.
/// </summary>
public sealed class RedisSupervisor
{
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    private readonly SupervisorSettings _settings;
    private readonly IRedisLauncher _launcher;
    private readonly ILogger _log;
    private readonly ILogger _output;
    private readonly RestartTracker _tracker;
    private readonly RedisControl? _control;
    private readonly LineRingBuffer _ring = new(200);
    private readonly TokenBucket _stderrBudget = new(20, TimeSpan.FromMinutes(1));
    private readonly CancellationTokenSource _force = new();
    private volatile IRedisProcess? _current;
    private volatile bool _stopping;
    private long _readyCount;
    private bool _credentialsWarningLogged;
    private bool _tlsWarningLogged;

    public RedisSupervisor(SupervisorSettings settings, IRedisLauncher launcher, ILogger lifecycleLogger, ILogger outputLogger, Func<double>? jitter = null)
    {
        _settings = settings;
        _launcher = launcher;
        _log = lifecycleLogger;
        _output = outputLogger;
        _tracker = new RestartTracker(settings.RestartPolicy, settings.MaxRestarts, settings.RestartWindow, jitter);
        _control = settings.Endpoint is null ? null : new RedisControl(settings.Endpoint);
    }

    /// <summary>PID of the running redis-server, if any.</summary>
    public int? CurrentProcessId => _current?.Id;

    /// <summary>How many times redis-server has become ready (for tests and diagnostics).</summary>
    public long ReadyCount => Interlocked.Read(ref _readyCount);

    /// <summary>Kills redis-server immediately (second Ctrl+C, or the host's own shutdown deadline).</summary>
    public void ForceStop()
    {
        _stopping = true;
        try { _force.Cancel(); } catch (ObjectDisposedException) { }
        _current?.Terminate();
    }

    /// <summary>Runs until <paramref name="stopToken"/> requests a graceful stop, or the restart policy ends supervision.</summary>
    public async Task<SupervisorResult> RunAsync(CancellationToken stopToken)
    {
        foreach (var warning in _settings.Warnings)
            _log.LogWarning(EventIds.ConfigWarning, "{Warning}", warning);

        ExitStatus? lastExit = null;
        while (true)
        {
            if (stopToken.IsCancellationRequested)
                return new SupervisorResult(0, true, false, lastExit, "Stop requested.");

            var logOffset = CrashExcerpt.FileLength(_settings.LogFilePath);
            _ring.Clear();
            var startedAt = DateTimeOffset.UtcNow;
            IRedisProcess process;
            try
            {
                process = _launcher.Start(new RedisLaunchSpec(_settings.RedisServerPath, _settings.Arguments, _settings.WorkingDirectory), OnStdout, OnStderr);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _log.LogError(EventIds.StartFailed, "Could not start redis-server ({Path}): {Message}", _settings.RedisServerPath, ex.Message);
                var launchDecision = _tracker.OnUnexpectedEnd(RunEnd.LaunchFailed, null, TimeSpan.Zero, DateTimeOffset.UtcNow);
                var launchResult = await ApplyDecisionAsync(launchDecision, null, stopToken).ConfigureAwait(false);
                if (launchResult is not null) return launchResult;
                continue;
            }

            using (process)
            {
                _current = process;
                _log.LogInformation(EventIds.Starting, "Started redis-server (PID {Pid}): {CommandLine}", process.Id, _settings.DescribeCommandLine());

                var (end, requested, detail) = await SuperviseRunAsync(process, startedAt, stopToken).ConfigureAwait(false);
                int code;
                try
                {
                    code = await process.Exited.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Only possible if even TerminateJobObject/SIGKILL failed; the job still kills it when the wrapper exits.
                    _log.LogError(EventIds.StopProblem, "redis-server (PID {Pid}) could not be terminated.", process.Id);
                    code = -1;
                }
                var status = ExitCodeDecoder.Decode(code, _settings.Flavor);
                lastExit = status;

                if (!await process.WaitForChildrenAsync(requested ? _settings.StopTimeout : TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false))
                {
                    _log.LogWarning(EventIds.StopProblem, "Child processes of redis-server (PID {Pid}) were still running after it exited and were terminated.", process.Id);
                    process.Terminate();
                }
                _current = null;

                if (requested)
                {
                    _log.LogInformation(EventIds.Stopped, "redis-server (PID {Pid}) {Status}.", process.Id, status.Describe());
                    return new SupervisorResult(0, true, false, status, "Stop requested.");
                }

                var uptime = DateTimeOffset.UtcNow - startedAt;
                var decision = _tracker.OnUnexpectedEnd(end, status, uptime, DateTimeOffset.UtcNow);
                LogRunEnd(process.Id, end, status, uptime, detail, logOffset);
                var result = await ApplyDecisionAsync(decision, status, stopToken).ConfigureAwait(false);
                if (result is not null) return result;
            }
        }
    }

    private async Task<SupervisorResult?> ApplyDecisionAsync(SupervisorDecision decision, ExitStatus? status, CancellationToken stopToken)
    {
        switch (decision)
        {
            case SupervisorDecision.Restart restart:
                _log.LogWarning(EventIds.Restarting,
                    "Restarting redis-server in {Delay:0.0} s (failure {Count} of {Max} allowed within {Window}).",
                    restart.Delay.TotalSeconds, restart.CrashesInWindow, _tracker.MaxCrashes, RestartTracker.FormatSpan(_tracker.Window));
                try
                {
                    await Task.Delay(restart.Delay, stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new SupervisorResult(0, true, false, status, "Stop requested while waiting to restart.");
                }
                return null;

            case SupervisorDecision.Stop stop when stop.ServiceExitCode != 0:
                _log.LogError(EventIds.GaveUp, "{Reason}", stop.Reason);
                return new SupervisorResult(stop.ServiceExitCode, false, true, status, stop.Reason);

            case SupervisorDecision.Stop stop:
                _log.LogWarning(EventIds.UnrequestedExit, "{Reason}", stop.Reason);
                return new SupervisorResult(0, false, false, status, stop.Reason);

            default:
                throw new UnreachableException();
        }
    }

    private void LogRunEnd(int pid, RunEnd end, ExitStatus status, TimeSpan uptime, string? detail, long logOffset)
    {
        if (end == RunEnd.Exited && status.IsClean)
            return; // The decision event explains it; a clean exit is not a crash.

        var what = end switch
        {
            RunEnd.Hung => $"stopped answering PING ({detail}) and was killed",
            RunEnd.StartTimeout => $"did not answer PING within {_settings.StartTimeout.TotalSeconds:0} s of starting and was killed",
            _ => status.Describe(),
        };
        var excerpt = BuildExcerpt(logOffset);
        var source = _settings.LogFilePath is null ? "captured output" : _settings.LogFilePath;
        _log.LogError(EventIds.Crashed,
            "redis-server (PID {Pid}) {What} after running for {Uptime}.{NewLine}{NewLine}Redis output ({Source}):{NewLine}{Excerpt}",
            pid, what, FormatUptime(uptime), Environment.NewLine, Environment.NewLine, source, Environment.NewLine,
            excerpt.Length == 0 ? "(none)" : excerpt);
    }

    private string BuildExcerpt(long logOffset)
    {
        var ring = _ring.Snapshot();
        IReadOnlyList<string>? log = _settings.LogFilePath is null ? null : CrashExcerpt.ReadLogTail(_settings.LogFilePath, logOffset);
        static bool HasReport(IReadOnlyList<string> lines) =>
            lines.Any(l => l.Contains("REDIS BUG REPORT START", StringComparison.Ordinal) || l.Contains("Guru Meditation", StringComparison.Ordinal));

        if (log is { Count: > 0 } && HasReport(log)) return CrashExcerpt.Build(log);
        if (HasReport(ring)) return CrashExcerpt.Build(ring);
        if (log is { Count: > 0 }) return CrashExcerpt.Build(log);
        return CrashExcerpt.Build(ring);
    }

    private static string FormatUptime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s"
        : $"{t.TotalSeconds:0.0} s";

    private async Task<(RunEnd End, bool Requested, string? Detail)> SuperviseRunAsync(IRedisProcess process, DateTimeOffset startedAt, CancellationToken stopToken)
    {
        var stopTask = WhenCancelled(stopToken);

        var readiness = await WaitReadyAsync(process, stopToken).ConfigureAwait(false);
        switch (readiness.State)
        {
            case ReadyState.Stopped:
                await GracefulStopAsync(process).ConfigureAwait(false);
                return (RunEnd.Exited, true, null);
            case ReadyState.Exited:
                return (RunEnd.Exited, stopToken.IsCancellationRequested, null);
            case ReadyState.TimedOut:
                _log.LogWarning(EventIds.StartFailed, "redis-server (PID {Pid}) did not answer PING on {Endpoint} within {Timeout:0} s ({Detail}); killing it.",
                    process.Id, _settings.Endpoint, _settings.StartTimeout.TotalSeconds, readiness.Detail);
                await KillAsync(process).ConfigureAwait(false);
                return (RunEnd.StartTimeout, false, readiness.Detail);
            case ReadyState.StillLoading:
                _log.LogWarning(EventIds.Ready, "redis-server (PID {Pid}) is alive but still not serving after {Timeout:0} s ({Detail}); supervision continues.",
                    process.Id, _settings.StartTimeout.TotalSeconds, readiness.Detail);
                break;
            case ReadyState.NoProbe:
                _log.LogInformation(EventIds.Ready, "redis-server (PID {Pid}) is running; it has no TCP port, so readiness cannot be checked.", process.Id);
                break;
            default:
                Interlocked.Increment(ref _readyCount);
                _log.LogInformation(EventIds.Ready, "redis-server (PID {Pid}) is ready on {Endpoint} after {Elapsed:0.0} s.",
                    process.Id, _settings.Endpoint, (DateTimeOffset.UtcNow - startedAt).TotalSeconds);
                break;
        }

        using var healthCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        var healthTask = MonitorHealthAsync(process, healthCts.Token);
        var done = await Task.WhenAny(process.Exited, healthTask, stopTask).ConfigureAwait(false);
        await healthCts.CancelAsync().ConfigureAwait(false);

        if (done == process.Exited)
            return (RunEnd.Exited, stopToken.IsCancellationRequested, null);

        // The health task also completes (with null) when the stop request cancels it, so the stop check comes first.
        var detail = done == healthTask ? await healthTask.ConfigureAwait(false) : null;
        if (done == stopTask || detail is null || stopToken.IsCancellationRequested)
        {
            await GracefulStopAsync(process).ConfigureAwait(false);
            return (RunEnd.Exited, true, null);
        }

        if (process.Exited.IsCompleted)
            return (RunEnd.Exited, stopToken.IsCancellationRequested, null);
        _log.LogWarning(EventIds.StopProblem, "redis-server (PID {Pid}) has not answered PING for {Failures} consecutive probes ({Detail}); killing it.",
            process.Id, _settings.HealthFailures, detail);
        await KillAsync(process).ConfigureAwait(false);
        return (RunEnd.Hung, false, detail);
    }

    private enum ReadyState { Ready, StillLoading, NoProbe, TimedOut, Exited, Stopped }

    private sealed record Readiness(ReadyState State, string Detail);

    private async Task<Readiness> WaitReadyAsync(IRedisProcess process, CancellationToken stopToken)
    {
        if (_control is null) return new Readiness(ReadyState.NoProbe, "");
        var sw = Stopwatch.StartNew();
        var alive = false;
        var detail = "no reply yet";
        while (true)
        {
            if (process.Exited.IsCompleted) return new Readiness(ReadyState.Exited, detail);
            if (stopToken.IsCancellationRequested) return new Readiness(ReadyState.Stopped, detail);

            var remaining = _settings.StartTimeout - sw.Elapsed;
            ProbeResult probe;
            try
            {
                var timeout = remaining < TimeSpan.FromSeconds(2) && remaining > TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromSeconds(2);
                probe = await _control.PingAsync(timeout, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new Readiness(ReadyState.Stopped, detail);
            }

            detail = probe.Detail;
            if (probe.Status == ProbeStatus.Ready) return new Readiness(ReadyState.Ready, detail);
            if (probe.Status == ProbeStatus.Alive)
            {
                alive = true;
                // Serving, but not to the wrapper: waiting for PONG would only run into the start timeout.
                if (probe.Issue != ProbeIssue.None)
                {
                    WarnProbeIssueOnce(probe);
                    return new Readiness(ReadyState.Ready, detail);
                }
            }

            if (sw.Elapsed >= _settings.StartTimeout)
                return new Readiness(alive ? ReadyState.StillLoading : ReadyState.TimedOut, detail);

            try
            {
                await Task.WhenAny(process.Exited, Task.Delay(ReadyPollInterval, stopToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new Readiness(ReadyState.Stopped, detail);
            }
        }
    }

    /// <summary>
    /// Completes with a description when the probe has failed <see cref="SupervisorSettings.HealthFailures"/> times in a row,
    /// or with null once <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    private async Task<string?> MonitorHealthAsync(IRedisProcess process, CancellationToken cancellationToken)
    {
        if (_control is null || _settings.HealthInterval <= TimeSpan.Zero)
        {
            await WhenCancelled(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var misses = 0;
        var lastDetail = "";
        // A fixed cadence (not "interval after the previous probe ended"), so N misses span N intervals
        // even when each probe runs into its timeout.
        using var timer = new PeriodicTimer(_settings.HealthInterval);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            if (_stopping || process.Exited.IsCompleted) continue;

            ProbeResult probe;
            try
            {
                probe = await _control.PingAsync(_settings.ProbeTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            if (_stopping) continue;

            if (probe.Status == ProbeStatus.Unresponsive)
            {
                misses++;
                lastDetail = probe.Detail;
                if (misses == 1 || misses % 4 == 0)
                    _log.LogDebug("Liveness probe missed ({Misses}/{Max}): {Detail}", misses, _settings.HealthFailures, probe.Detail);
                if (misses >= _settings.HealthFailures)
                    return $"{misses} missed probes over {(_settings.HealthInterval * misses).TotalSeconds:0} s; last: {lastDetail}";
                continue;
            }

            if (misses > 0)
                _log.LogInformation("redis-server answered again after {Misses} missed probe(s).", misses);
            misses = 0;
            if (probe.Issue != ProbeIssue.None)
                WarnProbeIssueOnce(probe);
        }
    }

    private void WarnProbeIssueOnce(ProbeResult probe)
    {
        switch (probe.Issue)
        {
            case ProbeIssue.Credentials when !_credentialsWarningLogged:
                _credentialsWarningLogged = true;
                _log.LogWarning(EventIds.ConfigWarning,
                    "The wrapper cannot authenticate to redis-server ({Detail}). Liveness still works, but a graceful SHUTDOWN will be refused, " +
                    "so stopping the service cannot run the final save. Keep requirepass in the config the service uses, or pass " +
                    "--auth-user / --auth-password-file (required when the password is set by an ACL 'user' line or an aclfile).", probe.Detail);
                break;
            case ProbeIssue.Tls when !_tlsWarningLogged:
                _tlsWarningLogged = true;
                _log.LogWarning(EventIds.ConfigWarning,
                    "The wrapper's TLS connection to redis-server failed ({Detail}). Redis accepted the TCP connection, so it is treated as alive, " +
                    "but the wrapper cannot send it any command: stopping the service will terminate it without the final save. The wrapper " +
                    "verifies the server certificate against tls-ca-cert-file and presents tls-client-cert-file (or tls-cert-file) as its " +
                    "client certificate; make both valid for this server.", probe.Detail);
                break;
        }
    }

    private async Task GracefulStopAsync(IRedisProcess process)
    {
        _stopping = true;
        var sw = Stopwatch.StartNew();
        var force = _force.Token;
        TimeSpan Remaining() => _settings.StopTimeout - sw.Elapsed is var r && r > TimeSpan.Zero ? r : TimeSpan.Zero;

        if (process.Exited.IsCompleted) return;
        _log.LogInformation(EventIds.Stopping, "Stopping redis-server (PID {Pid}) with SHUTDOWN (timeout {Timeout:0} s).", process.Id, _settings.StopTimeout.TotalSeconds);

        var terminateNow = false;
        if (_control is not null)
        {
            ShutdownResult result;
            try
            {
                result = await _control.ShutdownAsync(_settings.ShutdownForce, Remaining(), force).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new ShutdownResult(force.IsCancellationRequested ? ShutdownOutcome.Unreachable : ShutdownOutcome.Refused,
                    force.IsCancellationRequested ? "forced stop" : "no reply to SHUTDOWN within the stop timeout");
            }

            switch (result.Outcome)
            {
                case ShutdownOutcome.Accepted:
                    break;
                case ShutdownOutcome.AcceptedWithForce:
                    _log.LogWarning(EventIds.StopProblem, "SHUTDOWN could not save the dataset; SHUTDOWN FORCE was sent. Data written since the last successful save is lost. {Detail}", result.Detail);
                    break;
                case ShutdownOutcome.Refused when result.IsCredentialsProblem:
                    _log.LogError(EventIds.StopProblem,
                        "redis-server refused SHUTDOWN because the wrapper is not authenticated ({Detail}), so the final save could not be requested. " +
                        "Keep requirepass in the config the service uses, or pass --auth-user / --auth-password-file. It will be asked to exit " +
                        "through the operating system where that is possible, otherwise terminated; data written since the last successful save may be lost.",
                        result.Detail);
                    terminateNow = !process.Exited.IsCompleted && !process.TryRequestTermination();
                    break;
                case ShutdownOutcome.Refused when result.IsSaveFailure:
                    _log.LogError(EventIds.StopProblem,
                        "redis-server refused SHUTDOWN because the final save failed ({Detail}); see the Redis log for the reason. " +
                        "It will be terminated; data written since the last successful save is lost. {Hint}",
                        result.Detail, _settings.ShutdownForce
                            ? "SHUTDOWN FORCE was refused as well."
                            : "Pass --shutdown-force to let Redis exit without saving instead.");
                    terminateNow = !process.Exited.IsCompleted;
                    break;
                case ShutdownOutcome.Refused when result.ErrorCode.Length == 0:
                    _log.LogError(EventIds.StopProblem,
                        "SHUTDOWN did not complete ({Detail}). redis-server will be terminated; data written since the last successful save is lost.",
                        result.Detail);
                    terminateNow = !process.Exited.IsCompleted;
                    break;
                case ShutdownOutcome.Refused:
                    _log.LogError(EventIds.StopProblem,
                        "redis-server refused SHUTDOWN with an unexpected error ({Detail}). It will be terminated; data written since the last successful save is lost.",
                        result.Detail);
                    terminateNow = !process.Exited.IsCompleted;
                    break;
                case ShutdownOutcome.Unreachable:
                    _log.LogError(EventIds.StopProblem, "Could not send SHUTDOWN: {Detail}.", result.Detail);
                    terminateNow = !process.TryRequestTermination();
                    break;
            }
        }
        else if (!process.TryRequestTermination())
        {
            terminateNow = true;
        }

        if (!terminateNow)
        {
            try
            {
                await process.Exited.WaitAsync(Remaining(), force).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.LogError(EventIds.StopProblem,
                    "redis-server (PID {Pid}) did not exit within {Timeout:0} s of SHUTDOWN; terminating it. The final snapshot was NOT written; data since the last save is lost.",
                    process.Id, _settings.StopTimeout.TotalSeconds);
                terminateNow = true;
            }
            catch (OperationCanceledException)
            {
                terminateNow = true;
            }
        }

        if (terminateNow && !process.Exited.IsCompleted)
            await KillAsync(process).ConfigureAwait(false);
    }

    private static async Task KillAsync(IRedisProcess process)
    {
        process.Terminate();
        try
        {
            await process.Exited.WaitAsync(KillWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Nothing more can be done; the job object still kills it when the wrapper exits.
        }
    }

    private static Task WhenCancelled(CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), tcs);
        return tcs.Task;
    }

    private void OnStdout(string line)
    {
        _ring.Add(line);
        _output.LogDebug(EventIds.RedisStdout, "{Line}", line);
    }

    private void OnStderr(string line)
    {
        _ring.Add(line);
        if (!_stderrBudget.TryTake()) return;
        var suppressed = _stderrBudget.TakeSuppressedCount();
        if (suppressed > 0)
            _output.LogWarning(EventIds.RedisStderr, "{Line} ({Suppressed} earlier stderr lines were not logged: more than 20 per minute)", line, suppressed);
        else
            _output.LogWarning(EventIds.RedisStderr, "{Line}", line);
    }
}
