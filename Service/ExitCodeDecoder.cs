using System.Globalization;

namespace RedisService.Service;

public enum ExitKind
{
    /// <summary>exit(0): SHUTDOWN or a normal stop.</summary>
    Clean,

    /// <summary>exit(n) with n != 0, e.g. 1 for a config error or a failed bind.</summary>
    Exit,

    /// <summary>Terminated by a signal (SIGABRT from serverPanic, SIGSEGV, SIGKILL...).</summary>
    Signal,

    /// <summary>A Windows status that is not a Cygwin wait status (NTSTATUS crash code, TerminateProcess value).</summary>
    WindowsStatus,
}

public sealed record ExitStatus(ExitKind Kind, int RawCode, int Value, bool CoreDumped)
{
    public bool IsClean => Kind == ExitKind.Clean;

    public string Describe() => Kind switch
    {
        ExitKind.Clean => "exited cleanly (code 0)",
        ExitKind.Exit => $"exited with status {Value}",
        ExitKind.Signal => $"was terminated by signal {Value} ({ExitCodeDecoder.SignalName(Value)}){(CoreDumped ? " with core flag" : "")}, exit code 0x{RawCode:X}",
        _ => $"ended with Windows status 0x{unchecked((uint)RawCode):X8}{ExitCodeDecoder.NtStatusName(RawCode)}",
    };

    /// <summary>Conventional shell exit code for the foreground command: n for exit(n), 128+s for a signal.</summary>
    public int ShellExitCode => Kind switch
    {
        ExitKind.Clean => 0,
        ExitKind.Exit => Value,
        ExitKind.Signal => 128 + Value,
        _ => 1,
    };
}

public static class ExitCodeDecoder
{
    /// <summary>
    /// Decodes the exit code .NET sees for redis-server.
    /// Cygwin/MSYS2 (process not started by a Cygwin parent): pinfo.cc byte-swaps the wait status before
    /// ExitProcess, so exit(n) arrives as n (1-255) and death by signal s as s&lt;&lt;8, plus 0x8000 with the core flag.
    /// Native Unix (tests): .NET reports 128+s for a signal.
    /// </summary>
    public static ExitStatus Decode(int code, RuntimeFlavor flavor)
    {
        if (code == 0) return new ExitStatus(ExitKind.Clean, 0, 0, false);

        if (flavor == RuntimeFlavor.Native)
        {
            return code is > 128 and < 128 + 65
                ? new ExitStatus(ExitKind.Signal, code, code - 128, false)
                : new ExitStatus(ExitKind.Exit, code, code, false);
        }

        if (code is > 0 and <= 0xFF) return new ExitStatus(ExitKind.Exit, code, code, false);
        if (code is > 0xFF and <= 0xFFFF && (code & 0xFF) == 0)
        {
            var signal = (code >> 8) & 0x7F;
            if (signal is > 0 and < 65) return new ExitStatus(ExitKind.Signal, code, signal, (code & 0x8000) != 0);
        }
        return new ExitStatus(ExitKind.WindowsStatus, code, code, false);
    }

    public static string SignalName(int signal) => signal switch
    {
        1 => "SIGHUP",
        2 => "SIGINT",
        3 => "SIGQUIT",
        4 => "SIGILL",
        6 => "SIGABRT",
        7 => "SIGEMT",
        8 => "SIGFPE",
        9 => "SIGKILL",
        10 => "SIGBUS",
        11 => "SIGSEGV",
        13 => "SIGPIPE",
        15 => "SIGTERM",
        _ => "signal " + signal.ToString(CultureInfo.InvariantCulture),
    };

    public static string NtStatusName(int code) => unchecked((uint)code) switch
    {
        0xC0000005 => " (STATUS_ACCESS_VIOLATION)",
        0xC0000017 => " (STATUS_NO_MEMORY)",
        0xC00000FD => " (STATUS_STACK_OVERFLOW)",
        0xC0000135 => " (STATUS_DLL_NOT_FOUND)",
        0xC0000142 => " (STATUS_DLL_INIT_FAILED)",
        0xC0000409 => " (STATUS_STACK_BUFFER_OVERRUN)",
        0xFFFFFFFF => " (terminated)",
        _ => "",
    };
}
