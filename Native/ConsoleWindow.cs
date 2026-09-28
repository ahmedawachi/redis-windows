using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RedisService.Native;

/// <summary>
/// Whether this process has a console window of its own, which is what a double-click in Explorer gives it.
/// Started from cmd or PowerShell, the shell shares the console, so there is someone to read an error.
/// </summary>
public static class ConsoleWindow
{
    public static bool IsOwnedByThisProcess() => OperatingSystem.IsWindows() && CountAttachedProcesses() == 1;

    [SupportedOSPlatform("windows")]
    private static uint CountAttachedProcesses()
    {
        // redis-server runs without a console (DetachedProcessLauncher), so it never adds to this count.
        var ids = new uint[2];
        return GetConsoleProcessList(ids, (uint)ids.Length);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);
}
