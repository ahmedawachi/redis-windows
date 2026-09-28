using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RedisService.CommandLine;

namespace RedisService.Service;

/// <summary>Receives the exit code the service reports to the Service Control Manager.</summary>
public interface IServiceExitCode
{
    int ExitCode { get; }

    void Set(int exitCode);
}

public sealed class ProcessExitCode : IServiceExitCode
{
    public int ExitCode { get; private set; }

    public void Set(int exitCode)
    {
        ExitCode = exitCode;
        Environment.ExitCode = exitCode;
    }
}

/// <summary>
/// Hosts <see cref="RedisSupervisor"/> in the Windows service. When the supervisor gives up (crash loop, or a crash
/// under restart policy "never") it stores exit code 1067 and stops the host, so SCM sees a non-zero exit and runs
/// the failure actions (FailureActionsOnNonCrashFailures is set at install).
/// </summary>
public sealed class RedisHostedService(
    RunOptions options,
    IHostApplicationLifetime lifetime,
    IServiceExitCode exitCode,
    ILoggerFactory loggerFactory,
    Func<SupervisorSettings, RedisSupervisor> supervisorFactory) : BackgroundService
{
    private readonly ILogger _log = loggerFactory.CreateLogger("RedisService");
    private RedisSupervisor? _supervisor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SupervisorSettings settings;
        try
        {
            settings = SupervisorSettingsResolver.Resolve(options, AppContext.BaseDirectory, AppContext.BaseDirectory);
        }
        catch (WrapperConfigurationException ex)
        {
            _log.LogError(EventIds.StartFailed, "The service cannot start: {Message}", ex.Message);
            exitCode.Set(WrapperConfigurationException.ExitCode);
            lifetime.StopApplication();
            return;
        }

        _supervisor = supervisorFactory(settings);
        SupervisorResult result;
        try
        {
            result = await _supervisor.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(EventIds.GaveUp, ex, "The supervisor failed unexpectedly: {Message}", ex.Message);
            exitCode.Set(RestartTracker.CrashLoopExitCode);
            lifetime.StopApplication();
            return;
        }

        if (!result.StopRequested)
        {
            exitCode.Set(result.ExitCode);
            lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The host's shutdown deadline (stop timeout + margin) is the point where graceful stopping is over.
        await using var registration = cancellationToken.Register(() => _supervisor?.ForceStop());
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (ExecuteTask is { IsCompleted: false } task)
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false);
    }
}
