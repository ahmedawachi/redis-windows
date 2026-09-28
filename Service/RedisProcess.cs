using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RedisService.Native;

namespace RedisService.Service;

/// <summary>A running redis-server (plus, on Windows, the job holding its fork children).</summary>
public interface IRedisProcess : IDisposable
{
    int Id { get; }

    /// <summary>Completes with the raw exit code when redis-server exits.</summary>
    Task<int> Exited { get; }

    /// <summary>Last resort: kills redis-server and every process it created.</summary>
    void Terminate();

    /// <summary>Asks the process to exit through the OS (SIGTERM on Unix). Returns false where that is unavailable.</summary>
    bool TryRequestTermination();

    /// <summary>After redis-server exited, waits for its remaining children (a BGSAVE fork) to finish.</summary>
    Task<bool> WaitForChildrenAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record RedisLaunchSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

public interface IRedisLauncher
{
    IRedisProcess Start(RedisLaunchSpec spec, Action<string> onStdout, Action<string> onStderr);
}

/// <summary>
/// Picks the launcher for the platform. On Windows redis-server is started without a console
/// (<see cref="DetachedProcessLauncher"/>): with a console it receives CTRL_SHUTDOWN_EVENT at reboot and Cygwin lets
/// the default handler kill it before the wrapper's SHUTDOWN can save. Elsewhere System.Diagnostics.Process is used.
/// </summary>
public static class RedisLaunchers
{
    public static IRedisLauncher Create(Action<string>? warn)
    {
        if (OperatingSystem.IsWindows())
            return new DetachedProcessLauncher(warn);
        return new ManagedProcessLauncher();
    }
}

/// <summary>Launches through System.Diagnostics.Process. Used on non-Windows hosts (tests) and as the Windows fallback.</summary>
public sealed class ManagedProcessLauncher : IRedisLauncher
{
    public IRedisProcess Start(RedisLaunchSpec spec, Action<string> onStdout, Action<string> onStderr)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onStdout(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onStderr(e.Data); };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start '{spec.FileName}'.");
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        JobObject? job = null;
        if (OperatingSystem.IsWindows())
            job = JobObject.TryCreateForChild(process.Handle, null);
        return new ManagedRedisProcess(process, job);
    }

    private sealed class ManagedRedisProcess(Process process, JobObject? job) : IRedisProcess
    {
        private readonly Task<int> _exited = WaitAsync(process);

        public int Id { get; } = process.Id;

        public Task<int> Exited => _exited;

        private static async Task<int> WaitAsync(Process p)
        {
            await p.WaitForExitAsync().ConfigureAwait(false);
            return p.ExitCode;
        }

        public void Terminate()
        {
            if (job is not null && OperatingSystem.IsWindows())
            {
                job.Terminate(RestartTracker.CrashLoopExitCode);
                return;
            }
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }

        public bool TryRequestTermination()
        {
            if (OperatingSystem.IsWindows()) return false;
            try
            {
                return !process.HasExited && UnixSignals.Kill(process.Id, UnixSignals.SigTerm) == 0;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public async Task<bool> WaitForChildrenAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (job is null || !OperatingSystem.IsWindows()) return true;
            return await job.WaitForEmptyAsync(timeout, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            process.Dispose();
            if (OperatingSystem.IsWindows()) job?.Dispose();
        }
    }
}

internal static partial class UnixSignals
{
    public const int SigTerm = 15;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    [UnsupportedOSPlatform("windows")]
    private static extern int NativeKill(int pid, int sig);

    public static int Kill(int pid, int signal)
    {
        if (OperatingSystem.IsWindows()) return -1;
        return NativeKill(pid, signal);
    }
}
