using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RedisService.Service;

/// <summary>
/// WindowsServiceLifetime that (1) keeps asking SCM for more time while a stop is in progress, because a final
/// save can take longer than SCM's default wait hint, and (2) reports the supervisor's exit code (1067 after a
/// crash loop) as the service's Win32 exit code, which is what triggers the failure actions.
/// At system shutdown (OnShutdown) the service is still RUNNING, so SCM accepts no wait hints and the budget is the
/// system's service shutdown timeout; redis-server runs without a console, so CTRL_SHUTDOWN_EVENT cannot kill it
/// before the SHUTDOWN command reaches it. .NET's ServiceBase has no PRESHUTDOWN support.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RedisServiceLifetime(
    IHostEnvironment environment,
    IHostApplicationLifetime applicationLifetime,
    ILoggerFactory loggerFactory,
    IOptions<HostOptions> hostOptions,
    IOptions<WindowsServiceLifetimeOptions> serviceOptions)
    : WindowsServiceLifetime(environment, applicationLifetime, loggerFactory, hostOptions, serviceOptions), IServiceExitCode
{
    private static readonly TimeSpan HintInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Hint = TimeSpan.FromSeconds(30);

    int IServiceExitCode.ExitCode => ExitCode;

    public void Set(int exitCode)
    {
        ExitCode = exitCode;
        Environment.ExitCode = exitCode;
    }

    protected override void OnStop()
    {
        using var keepAlive = new Timer(_ => RequestMoreTime(), null, TimeSpan.Zero, HintInterval);
        base.OnStop();
    }

    private void RequestMoreTime()
    {
        try
        {
            RequestAdditionalTime(Hint);
        }
        catch (InvalidOperationException)
        {
            // Not in a pending state (already stopped).
        }
    }
}
