using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RedisService.Native;

/// <summary>Service Control Manager operations used by install and uninstall (P/Invoke, no reflection).</summary>
[SupportedOSPlatform("windows")]
public static class ServiceManager
{
    public sealed record ServiceDefinition(
        string Name,
        string DisplayName,
        string Description,
        string BinaryPath,
        string StartMode,
        bool DelayedStart,
        string? Account);

    /// <summary>Failure actions: restart after 5 s, 30 s, 60 s; reset the failure count after one day.</summary>
    public static readonly (int DelayMs, int Type)[] DefaultFailureActions = [(5000, SC_ACTION_RESTART), (30000, SC_ACTION_RESTART), (60000, SC_ACTION_RESTART)];
    public const int FailureResetSeconds = 86400;

    /// <summary>Creates the service and applies description, failure actions, the non-crash failure flag, delayed start and SID type.</summary>
    public static void Install(ServiceDefinition definition)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the Service Control Manager. Run this command as administrator.");
        try
        {
            var startType = definition.StartMode switch
            {
                "manual" => SERVICE_DEMAND_START,
                "disabled" => SERVICE_DISABLED,
                _ => SERVICE_AUTO_START,
            };
            var service = CreateServiceW(scm, definition.Name, definition.DisplayName, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS,
                startType, SERVICE_ERROR_NORMAL, definition.BinaryPath, null, IntPtr.Zero, null, definition.Account, null);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ERROR_SERVICE_EXISTS)
                    throw new InvalidOperationException($"Service '{definition.Name}' already exists. Uninstall it first or choose another --service-name.");
                throw new Win32Exception(error, "CreateService failed");
            }

            try
            {
                SetDescription(service, definition.Description);
                SetFailureActions(service);
                SetConfig(service, SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, new SERVICE_FAILURE_ACTIONS_FLAG { fFailureActionsOnNonCrashFailures = 1 });
                SetConfig(service, SERVICE_CONFIG_SERVICE_SID_INFO, new SERVICE_SID_INFO { dwServiceSidType = SERVICE_SID_TYPE_UNRESTRICTED });
                if (definition.DelayedStart && startType == SERVICE_AUTO_START)
                    SetConfig(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = 1 });
            }
            catch
            {
                DeleteService(service);
                CloseServiceHandle(service);
                throw;
            }
            CloseServiceHandle(service);
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>Asks the service to stop and waits up to <paramref name="timeout"/>. Returns true once it is stopped.</summary>
    public static bool StopAndWait(string name, TimeSpan timeout, Action<string>? progress = null)
    {
        return WithService(name, SERVICE_STOP | SERVICE_QUERY_STATUS, service =>
        {
            var status = new SERVICE_STATUS();
            if (!QueryServiceStatus(service, ref status))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryServiceStatus failed");
            if (status.dwCurrentState == SERVICE_STOPPED) return true;

            if (status.dwCurrentState != SERVICE_STOP_PENDING && !ControlService(service, SERVICE_CONTROL_STOP, ref status))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_SERVICE_NOT_ACTIVE)
                    throw new Win32Exception(error, "Stopping the service failed");
                return true;
            }

            var deadline = DateTime.UtcNow + timeout;
            var lastReport = DateTime.UtcNow;
            while (DateTime.UtcNow < deadline)
            {
                if (!QueryServiceStatus(service, ref status))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryServiceStatus failed");
                if (status.dwCurrentState == SERVICE_STOPPED) return true;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(10))
                {
                    progress?.Invoke("Still waiting for the service to stop (Redis may be saving its dataset)...");
                    lastReport = DateTime.UtcNow;
                }
                Thread.Sleep(500);
            }
            return false;
        });
    }

    public static void Delete(string name)
    {
        WithService(name, DELETE, service =>
        {
            if (!DeleteService(service))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ERROR_SERVICE_MARKED_FOR_DELETE)
                    throw new InvalidOperationException($"Service '{name}' is already marked for deletion; it disappears once every handle to it is closed (close services.msc) or after a reboot.");
                throw new Win32Exception(error, "DeleteService failed");
            }
            return true;
        });
    }

    public static void Start(string name)
    {
        WithService(name, SERVICE_START, service =>
        {
            if (!StartServiceW(service, 0, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StartService failed");
            return true;
        });
    }

    public static bool Exists(string name)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return false;
        try
        {
            var service = OpenServiceW(scm, name, SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero) return false;
            CloseServiceHandle(service);
            return true;
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static T WithService<T>(string name, uint access, Func<IntPtr, T> action)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the Service Control Manager. Run this command as administrator.");
        try
        {
            var service = OpenServiceW(scm, name, access);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ERROR_SERVICE_DOES_NOT_EXIST)
                    throw new InvalidOperationException($"Service '{name}' does not exist.");
                throw new Win32Exception(error, $"Cannot open service '{name}'");
            }
            try
            {
                return action(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static void SetDescription(IntPtr service, string description)
    {
        var text = Marshal.StringToHGlobalUni(description);
        try
        {
            SetConfig(service, SERVICE_CONFIG_DESCRIPTION, new SERVICE_DESCRIPTIONW { lpDescription = text });
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private static void SetFailureActions(IntPtr service)
    {
        var size = Marshal.SizeOf<SC_ACTION>();
        var actions = Marshal.AllocHGlobal(size * DefaultFailureActions.Length);
        try
        {
            for (var i = 0; i < DefaultFailureActions.Length; i++)
            {
                var (delay, type) = DefaultFailureActions[i];
                Marshal.StructureToPtr(new SC_ACTION { Type = type, Delay = (uint)delay }, actions + i * size, false);
            }
            SetConfig(service, SERVICE_CONFIG_FAILURE_ACTIONS, new SERVICE_FAILURE_ACTIONSW
            {
                dwResetPeriod = FailureResetSeconds,
                cActions = (uint)DefaultFailureActions.Length,
                lpsaActions = actions,
            });
        }
        finally
        {
            Marshal.FreeHGlobal(actions);
        }
    }

    private static void SetConfig<T>(IntPtr service, uint level, T info) where T : struct
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!ChangeServiceConfig2W(service, level, buffer))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"ChangeServiceConfig2 (level {level}) failed");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint DELETE = 0x10000;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    private const uint SERVICE_AUTO_START = 0x2;
    private const uint SERVICE_DEMAND_START = 0x3;
    private const uint SERVICE_DISABLED = 0x4;
    private const uint SERVICE_ERROR_NORMAL = 0x1;
    private const uint SERVICE_CONTROL_STOP = 0x1;
    private const uint SERVICE_STOPPED = 0x1;
    private const uint SERVICE_STOP_PENDING = 0x3;
    private const uint SERVICE_CONFIG_DESCRIPTION = 1;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS_FLAG = 4;
    private const uint SERVICE_CONFIG_SERVICE_SID_INFO = 5;
    private const uint SERVICE_SID_TYPE_UNRESTRICTED = 1;
    public const int SC_ACTION_RESTART = 1;
    private const int ERROR_SERVICE_EXISTS = 1073;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DESCRIPTIONW
    {
        public IntPtr lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONSW
    {
        public uint dwResetPeriod;
        public IntPtr lpRebootMsg;
        public IntPtr lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONS_FLAG
    {
        public int fFailureActionsOnNonCrashFailures;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DELAYED_AUTO_START_INFO
    {
        public int fDelayedAutostart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_SID_INFO
    {
        public uint dwServiceSidType;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateServiceW(IntPtr hSCManager, string serviceName, string? displayName, uint desiredAccess, uint serviceType,
        uint startType, uint errorControl, string binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(IntPtr hService, uint infoLevel, IntPtr info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr hService, uint numServiceArgs, IntPtr serviceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr hService, uint control, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS status);
}
