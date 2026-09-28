using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RedisService.Native;

/// <summary>
/// Win32 job object. Flags are KILL_ON_JOB_CLOSE | DIE_ON_UNHANDLED_EXCEPTION only:
/// no breakaway flags (Cygwin's exec adds CREATE_BREAKAWAY_FROM_JOB when the job allows it, spawn.cc) and no memory
/// limits (a job memory limit fails allocations directly, the very failure this wrapper exists to survive).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JobObject : IDisposable
{
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x400;
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;

    private IntPtr _handle;

    private JobObject(IntPtr handle) => _handle = handle;

    public IntPtr Handle => _handle;

    /// <summary>Creates a job with the wrapper's limit flags.</summary>
    public static JobObject Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION,
            },
        };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(handle);
            throw new Win32Exception(error, "SetInformationJobObject failed");
        }
        return new JobObject(handle);
    }

    /// <summary>
    /// Puts the wrapper itself in a kill-on-close job before any child starts, so every descendant (redis-server,
    /// its fork children) is born inside it and dies with the wrapper however the wrapper ends. The handle is kept
    /// for the life of the process on purpose.
    /// </summary>
    public static JobObject? AssignCurrentProcess(Action<string>? warn)
    {
        try
        {
            var job = Create();
            if (!AssignProcessToJobObject(job._handle, GetCurrentProcess()))
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                warn?.Invoke($"Could not place the wrapper in a job object (error {error}); redis-server will not be killed automatically if the wrapper itself is killed.");
                return null;
            }
            return job;
        }
        catch (Win32Exception ex)
        {
            warn?.Invoke($"Could not create a job object ({ex.Message}); redis-server will not be killed automatically if the wrapper itself is killed.");
            return null;
        }
    }

    /// <summary>Creates a (nested) job for one redis-server run and assigns the process to it.</summary>
    public static JobObject? TryCreateForChild(IntPtr processHandle, Action<string>? warn)
    {
        try
        {
            var job = Create();
            if (!AssignProcessToJobObject(job._handle, processHandle))
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                warn?.Invoke($"Could not assign redis-server to its job object (error {error}); the last-resort kill falls back to the process only.");
                return null;
            }
            return job;
        }
        catch (Win32Exception ex)
        {
            warn?.Invoke($"Could not create the redis-server job object ({ex.Message}).");
            return null;
        }
    }

    public bool Assign(IntPtr processHandle) => AssignProcessToJobObject(_handle, processHandle);

    /// <summary>Kills every process in the job.</summary>
    public void Terminate(int exitCode)
    {
        if (_handle != IntPtr.Zero) TerminateJobObject(_handle, unchecked((uint)exitCode));
    }

    /// <summary>Number of processes currently in the job, or -1 if it cannot be queried.</summary>
    public int ActiveProcesses
    {
        get
        {
            if (_handle == IntPtr.Zero) return 0;
            return QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info,
                (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
                ? (int)info.ActiveProcesses
                : -1;
        }
    }

    /// <summary>Waits until no process is left in the job. Returns false on timeout.</summary>
    public async Task<bool> WaitForEmptyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var active = ActiveProcesses;
            if (active <= 0) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero) CloseHandle(h);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr hJob, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
