namespace RedisService.CommandLine;

/// <summary>What the wrapper does when redis-server exits without being asked to.</summary>
public enum RestartPolicy
{
    /// <summary>Restart after crashes and hangs; a clean exit nobody requested (e.g. a client SHUTDOWN) stops the service.</summary>
    OnCrash,

    /// <summary>Restart after every unrequested exit, including a clean one.</summary>
    Always,

    /// <summary>Never restart. A crash stops the service with exit code 1067 so SCM recovery actions can run.</summary>
    Never,
}

public enum CommandType
{
    Help,
    Version,
    Install,
    Uninstall,
    Run,
}

public abstract class CommandResult(CommandType type)
{
    public CommandType Type { get; } = type;
}

public sealed class HelpCommand() : CommandResult(CommandType.Help);

public sealed class VersionCommand() : CommandResult(CommandType.Version);

/// <summary>Options that control how redis-server is launched and supervised.</summary>
public class RunOptions
{
    /// <summary>Config file as typed by the user; null means "redis.conf next to RedisService.exe".</summary>
    public string? ConfigFilePath { get; set; }

    public int? Port { get; set; }

    public string? DataDirectory { get; set; }

    public string? LogLevel { get; set; }

    /// <summary>Overrides the Redis <c>logfile</c> setting.</summary>
    public string? LogFile { get; set; }

    public bool Foreground { get; set; }

    /// <summary>Service name; also the Event Log source and the registry key holding stored arguments.</summary>
    public string ServiceName { get; set; } = "Redis";

    public bool ServiceNameSpecified { get; set; }

    /// <summary>Path of redis-server; null means redis-server(.exe) next to RedisService.exe.</summary>
    public string? RedisServerPath { get; set; }

    public RestartPolicy RestartPolicy { get; set; } = RestartPolicy.OnCrash;

    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Interval between liveness probes; zero disables the probe.</summary>
    public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int HealthFailures { get; set; } = 12;

    public int MaxRestarts { get; set; } = 5;

    public TimeSpan RestartWindow { get; set; } = TimeSpan.FromMinutes(10);

    public string? AuthUser { get; set; }

    public string? AuthPasswordFile { get; set; }

    /// <summary>Send SHUTDOWN FORCE when SHUTDOWN fails (the final save failed). Data since the last save is then lost.</summary>
    public bool ShutdownForce { get; set; }
}

public sealed class RunCommand(RunOptions options) : CommandResult(CommandType.Run)
{
    public RunOptions Options { get; } = options;
}

public sealed class InstallOptions : RunOptions
{
    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    /// <summary>auto, manual or disabled.</summary>
    public string StartMode { get; set; } = "auto";

    public bool DelayedStart { get; set; }

    /// <summary>Run as the virtual account NT SERVICE\&lt;name&gt; instead of LocalSystem.</summary>
    public bool VirtualAccount { get; set; }
}

public sealed class InstallCommand(InstallOptions options) : CommandResult(CommandType.Install)
{
    public InstallOptions Options { get; } = options;
}

public sealed class UninstallOptions
{
    public string ServiceName { get; set; } = "Redis";

    /// <summary>How long to wait for the running service to stop before deleting it.</summary>
    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(150);
}

public sealed class UninstallCommand(UninstallOptions options) : CommandResult(CommandType.Uninstall)
{
    public UninstallOptions Options { get; } = options;
}
