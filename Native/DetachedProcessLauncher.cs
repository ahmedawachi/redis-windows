using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RedisService.Service;

namespace RedisService.Native;

/// <summary>
/// Starts redis-server with CreateProcessW(DETACHED_PROCESS | CREATE_SUSPENDED): no console at all, so no
/// CTRL_SHUTDOWN_EVENT at reboot (Cygwin returns FALSE for it and the default handler would kill Redis before the
/// wrapper's SHUTDOWN saves) and no conhost.exe per process. Cygwin's fork also creates the BGSAVE child detached when
/// the parent has no console (fork.cc). The process is put in its own nested job while still suspended, so there is no
/// window in which a fork child could escape the last-resort TerminateJobObject. Only the three stdio handles are
/// inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DetachedProcessLauncher(Action<string>? warn) : IRedisLauncher
{
    public IRedisProcess Start(RedisLaunchSpec spec, Action<string> onStdout, Action<string> onStderr)
    {
        try
        {
            return StartDetached(spec, onStdout, onStderr);
        }
        catch (Win32Exception ex)
        {
            warn?.Invoke($"Starting redis-server without a console failed ({ex.Message}); falling back to a hidden console.");
            return new ManagedProcessLauncher().Start(spec, onStdout, onStderr);
        }
    }

    private IRedisProcess StartDetached(RedisLaunchSpec spec, Action<string> onStdout, Action<string> onStderr)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero, errRead = IntPtr.Zero, errWrite = IntPtr.Zero, nul = IntPtr.Zero;
        IntPtr attrList = IntPtr.Zero, handleArray = IntPtr.Zero;
        var attrListInitialized = false;
        var pi = default(PROCESS_INFORMATION);
        var success = false;
        try
        {
            CreateInheritablePipe(ref sa, out outRead, out outWrite);
            CreateInheritablePipe(ref sa, out errRead, out errWrite);
            nul = CreateFileW("NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, ref sa, OPEN_EXISTING, 0, IntPtr.Zero);
            if (nul == INVALID_HANDLE_VALUE)
            {
                nul = IntPtr.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Opening NUL failed");
            }

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
            attrListInitialized = true;

            handleArray = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleArray, 0, nul);
            Marshal.WriteIntPtr(handleArray, IntPtr.Size, outWrite);
            Marshal.WriteIntPtr(handleArray, IntPtr.Size * 2, errWrite);
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleArray, IntPtr.Size * 3, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            var si = new STARTUPINFOEXW
            {
                StartupInfo = new STARTUPINFOW
                {
                    cb = Marshal.SizeOf<STARTUPINFOEXW>(),
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = nul,
                    hStdOutput = outWrite,
                    hStdError = errWrite,
                },
                lpAttributeList = attrList,
            };

            var commandLine = new StringBuilder(WindowsCommandLine.BuildCygwin(spec.FileName, spec.Arguments));
            const uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | DETACHED_PROCESS | CREATE_UNICODE_ENVIRONMENT;
            if (!CreateProcessW(spec.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, spec.WorkingDirectory, ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess '{spec.FileName}' failed");

            var job = JobObject.TryCreateForChild(pi.hProcess, warn);
            if (ResumeThread(pi.hThread) == uint.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                TerminateProcess(pi.hProcess, 1);
                job?.Dispose();
                throw new Win32Exception(error, "ResumeThread failed");
            }
            CloseHandle(pi.hThread);
            pi.hThread = IntPtr.Zero;

            var process = new DetachedRedisProcess(pi.dwProcessId, new SafeProcessHandle(pi.hProcess, ownsHandle: true), job,
                ReadLinesAsync(outRead, onStdout), ReadLinesAsync(errRead, onStderr));
            outRead = errRead = IntPtr.Zero; // Owned by the readers now.
            pi.hProcess = IntPtr.Zero;
            success = true;
            return process;
        }
        finally
        {
            // The child holds its own copies; the parent must close the write ends or the readers never see EOF.
            CloseIfSet(outWrite);
            CloseIfSet(errWrite);
            CloseIfSet(nul);
            if (!success)
            {
                CloseIfSet(outRead);
                CloseIfSet(errRead);
                CloseIfSet(pi.hThread);
                CloseIfSet(pi.hProcess);
            }
            if (attrListInitialized) DeleteProcThreadAttributeList(attrList);
            if (attrList != IntPtr.Zero) Marshal.FreeHGlobal(attrList);
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
        }
    }

    private static void CreateInheritablePipe(ref SECURITY_ATTRIBUTES sa, out IntPtr read, out IntPtr write)
    {
        if (!CreatePipe(out read, out write, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
        // Only the child's end may be inherited.
        if (!SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation failed");
    }

    private static Task ReadLinesAsync(IntPtr readHandle, Action<string> onLine)
    {
        var stream = new FileStream(new SafeFileHandle(readHandle, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
        return Task.Factory.StartNew(() =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            try
            {
                while (reader.ReadLine() is { } line) onLine(line);
            }
            catch (IOException)
            {
                // Broken pipe: the writer is gone.
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static void CloseIfSet(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
    }

    private sealed class DetachedRedisProcess : IRedisProcess
    {
        private readonly SafeProcessHandle _handle;
        private readonly JobObject? _job;
        private readonly Task<int> _exited;

        public DetachedRedisProcess(int pid, SafeProcessHandle handle, JobObject? job, Task stdout, Task stderr)
        {
            Id = pid;
            _handle = handle;
            _job = job;
            _exited = WaitForExitAsync(handle, stdout, stderr);
        }

        public int Id { get; }

        public Task<int> Exited => _exited;

        private static async Task<int> WaitForExitAsync(SafeProcessHandle handle, Task stdout, Task stderr)
        {
            using var wait = new ProcessWaitHandle(handle);
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => tcs.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Unregister(null);
            }
            // Let the last output lines (a crash report on stdout) arrive, but do not wait for a surviving fork
            // child that still holds the pipe.
            await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(1000)).ConfigureAwait(false);
            return GetExitCodeProcess(handle, out var code) ? unchecked((int)code) : -1;
        }

        public void Terminate()
        {
            if (_job is not null)
                _job.Terminate(RestartTracker.CrashLoopExitCode);
            else if (!_handle.IsClosed)
                TerminateProcess(_handle.DangerousGetHandle(), unchecked((uint)RestartTracker.CrashLoopExitCode));
        }

        public bool TryRequestTermination() => false;

        public Task<bool> WaitForChildrenAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            _job is null ? Task.FromResult(true) : _job.WaitForEmptyAsync(timeout, cancellationToken);

        public void Dispose()
        {
            _handle.Dispose();
            _job?.Dispose();
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint HANDLE_FLAG_INHERIT = 0x1;
    private const int STARTF_USESTDHANDLES = 0x100;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000;
    private const uint CREATE_SUSPENDED = 0x4;
    private const uint DETACHED_PROCESS = 0x8;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
