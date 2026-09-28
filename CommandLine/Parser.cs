using System.Globalization;

namespace RedisService.CommandLine;

/// <summary>Invalid command line. Main prints the message and exits with code 2.</summary>
public sealed class CommandLineException(string message) : Exception(message)
{
    public const int ExitCode = 2;
}

/// <summary>Strict parser: unknown options and invalid values are errors, never silently ignored.</summary>
public static class CommandLineParser
{
    [Flags]
    private enum Scope
    {
        Run = 1,
        Install = 2,
        Uninstall = 4,
        RunOrInstall = Run | Install,
        All = Run | Install | Uninstall,
    }

    private sealed record OptionSpec(string[] Names, bool TakesValue, Scope Scope, string ValueName, string Help);

    private static readonly OptionSpec[] Specs =
    [
        new(["-c", "--config"], true, Scope.RunOrInstall, "FILE", "Redis config file (default: redis.conf next to RedisService.exe)"),
        new(["--port"], true, Scope.RunOrInstall, "PORT", "Override the Redis port (0-65535)"),
        new(["--dir"], true, Scope.RunOrInstall, "DIR", "Override the Redis data directory"),
        new(["--loglevel"], true, Scope.RunOrInstall, "LEVEL", "Override the Redis log level (debug, verbose, notice, warning, nothing)"),
        new(["--logfile"], true, Scope.RunOrInstall, "FILE", "Override the Redis logfile"),
        new(["-f", "--foreground"], false, Scope.Run, "", "Run in the console instead of as a service"),
        new(["--service-name"], true, Scope.All, "NAME", "Service name (default: Redis)"),
        new(["--display-name"], true, Scope.Install, "NAME", "Service display name"),
        new(["--description"], true, Scope.Install, "TEXT", "Service description"),
        new(["--start-mode"], true, Scope.Install, "MODE", "Startup type: auto, manual, disabled (default: auto)"),
        new(["--delayed-start"], false, Scope.Install, "", "Use delayed automatic start"),
        new(["--virtual-account"], false, Scope.Install, "", "Run as NT SERVICE\\<name> instead of LocalSystem and grant it access to the data, log and config folders"),
        new(["--restart-policy"], true, Scope.RunOrInstall, "POLICY", "on-crash (default), always or never"),
        new(["--stop-timeout"], true, Scope.All, "TIME", "Time allowed for a graceful shutdown (default: 120s)"),
        new(["--start-timeout"], true, Scope.RunOrInstall, "TIME", "Time allowed for redis-server to answer PING after start (default: 120s)"),
        new(["--health-interval"], true, Scope.RunOrInstall, "TIME", "Liveness probe interval, 0 disables (default: 5s)"),
        new(["--health-failures"], true, Scope.RunOrInstall, "N", "Consecutive missed probes before a restart (default: 12)"),
        new(["--max-restarts"], true, Scope.RunOrInstall, "N", "Crashes allowed within --restart-window before giving up (default: 5)"),
        new(["--restart-window"], true, Scope.RunOrInstall, "TIME", "Window for --max-restarts (default: 10m)"),
        new(["--auth-user"], true, Scope.RunOrInstall, "USER", "ACL user for the wrapper's own connections (default: default user)"),
        new(["--auth-password-file"], true, Scope.RunOrInstall, "FILE", "File holding the password for the wrapper's own connections (default: requirepass)"),
        new(["--shutdown-force"], false, Scope.RunOrInstall, "", "If SHUTDOWN fails because the final save failed, send SHUTDOWN FORCE"),
        new(["--redis-server"], true, Scope.RunOrInstall, "FILE", "Path of redis-server (default: next to RedisService.exe)"),
    ];

    public static CommandResult Parse(string[] args)
    {
        // No arguments: under the Service Control Manager (a service created without arguments) run as the service,
        // otherwise (a double-click in Explorer) in this window, with redis.conf from this folder.
        if (args.Length == 0)
            return new RunCommand(new RunOptions());

        // Anywhere on the line, for compatibility with the previous parser.
        if (args.Any(a => a is "-h" or "--help" or "/?"))
            return new HelpCommand();
        if (args.Any(a => a is "-v" or "--version"))
            return new VersionCommand();

        var first = args[0].ToLowerInvariant();
        return first switch
        {
            "install" => new InstallCommand(ParseInstall(args.AsSpan(1))),
            "uninstall" => new UninstallCommand(ParseUninstall(args.AsSpan(1))),
            "run" => new RunCommand(ParseRun(args.AsSpan(1))),
            _ when first.StartsWith('-') => new RunCommand(ParseRun(args)),
            _ => throw new CommandLineException($"Unknown command '{args[0]}'. Expected install, uninstall or run."),
        };
    }

    public static RunOptions ParseRun(ReadOnlySpan<string> args)
    {
        var options = new RunOptions();
        Apply(args, Scope.Run, (name, value) => ApplyRunOption(options, name, value) ? true : throw Unreachable(name));
        return options;
    }

    /// <summary>Applies arguments on top of existing options (used to layer command-line flags over stored service parameters).</summary>
    public static void ApplyRun(RunOptions options, ReadOnlySpan<string> args) =>
        Apply(args, Scope.Run, (name, value) => ApplyRunOption(options, name, value) ? true : throw Unreachable(name));

    private static InstallOptions ParseInstall(ReadOnlySpan<string> args)
    {
        var options = new InstallOptions();
        Apply(args, Scope.Install, (name, value) =>
        {
            if (ApplyRunOption(options, name, value))
                return true;
            switch (name)
            {
                case "--display-name": options.DisplayName = RequireNonEmpty(name, value); return true;
                case "--description": options.Description = value; return true;
                case "--start-mode":
                    options.StartMode = value!.ToLowerInvariant() switch
                    {
                        "auto" or "manual" or "disabled" => value.ToLowerInvariant(),
                        _ => throw new CommandLineException($"Invalid value '{value}' for --start-mode. Expected auto, manual or disabled."),
                    };
                    return true;
                case "--delayed-start": options.DelayedStart = true; return true;
                case "--virtual-account": options.VirtualAccount = true; return true;
                default: throw Unreachable(name);
            }
        });
        if (options.DelayedStart && options.StartMode != "auto")
            throw new CommandLineException("--delayed-start requires --start-mode auto.");
        return options;
    }

    private static UninstallOptions ParseUninstall(ReadOnlySpan<string> args)
    {
        var options = new UninstallOptions();
        Apply(args, Scope.Uninstall, (name, value) =>
        {
            switch (name)
            {
                case "--service-name": options.ServiceName = ParseServiceName(value!); return true;
                case "--stop-timeout": options.StopTimeout = ParseDuration(name, value!, allowZero: false); return true;
                default: throw Unreachable(name);
            }
        });
        return options;
    }

    private static bool ApplyRunOption(RunOptions o, string name, string? value)
    {
        switch (name)
        {
            case "-c": o.ConfigFilePath = RequireNonEmpty(name, value); return true;
            case "--port": o.Port = ParseInt(name, value!, 0, 65535); return true;
            case "--dir": o.DataDirectory = RequireNonEmpty(name, value); return true;
            case "--loglevel":
                o.LogLevel = value!.ToLowerInvariant() switch
                {
                    "debug" or "verbose" or "notice" or "warning" or "nothing" => value.ToLowerInvariant(),
                    _ => throw new CommandLineException($"Invalid value '{value}' for --loglevel. Expected debug, verbose, notice, warning or nothing."),
                };
                return true;
            case "--logfile": o.LogFile = RequireNonEmpty(name, value); return true;
            case "-f": o.Foreground = true; return true;
            case "--service-name": o.ServiceName = ParseServiceName(value!); o.ServiceNameSpecified = true; return true;
            case "--restart-policy": o.RestartPolicy = ParseRestartPolicy(value!); return true;
            case "--stop-timeout": o.StopTimeout = ParseDuration(name, value!, allowZero: false); return true;
            case "--start-timeout": o.StartTimeout = ParseDuration(name, value!, allowZero: false); return true;
            case "--health-interval": o.HealthInterval = ParseDuration(name, value!, allowZero: true); return true;
            case "--health-failures": o.HealthFailures = ParseInt(name, value!, 1, 10_000); return true;
            case "--max-restarts": o.MaxRestarts = ParseInt(name, value!, 1, 10_000); return true;
            case "--restart-window": o.RestartWindow = ParseDuration(name, value!, allowZero: false); return true;
            case "--auth-user": o.AuthUser = RequireNonEmpty(name, value); return true;
            case "--auth-password-file": o.AuthPasswordFile = RequireNonEmpty(name, value); return true;
            case "--shutdown-force": o.ShutdownForce = true; return true;
            case "--redis-server": o.RedisServerPath = RequireNonEmpty(name, value); return true;
            default: return false;
        }
    }

    private static void Apply(ReadOnlySpan<string> args, Scope scope, Func<string, string?, bool> apply)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            string? inlineValue = null;
            var nameText = raw;
            var eq = raw.IndexOf('=');
            if (raw.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                nameText = raw[..eq];
                inlineValue = raw[(eq + 1)..];
            }

            var spec = Find(nameText);
            if (spec is null)
            {
                throw raw.StartsWith('-')
                    ? new CommandLineException($"Unknown option '{nameText}'. Run 'RedisService.exe --help' for the list of options.")
                    : new CommandLineException($"Unexpected argument '{raw}'. Values must follow an option such as --dir.");
            }
            if ((spec.Scope & scope) == 0)
                throw new CommandLineException($"Option '{nameText}' is not valid for this command.");

            string? value = null;
            if (spec.TakesValue)
            {
                if (inlineValue is not null)
                    value = inlineValue;
                else if (i + 1 < args.Length)
                    value = args[++i];
                else
                    throw new CommandLineException($"Option '{nameText}' requires a value ({spec.ValueName}).");
            }
            else if (inlineValue is not null)
            {
                throw new CommandLineException($"Option '{nameText}' does not take a value.");
            }

            apply(spec.Names[0], value);
        }
    }

    private static OptionSpec? Find(string name) =>
        Specs.FirstOrDefault(s => s.Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));

    private static Exception Unreachable(string name) => new CommandLineException($"Option '{name}' is not valid for this command.");

    private static string RequireNonEmpty(string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? throw new CommandLineException($"Option '{name}' requires a non-empty value.") : value;

    private static string ParseServiceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.IndexOfAny(['/', '\\']) >= 0)
            throw new CommandLineException($"Invalid service name '{value}'. It must be 1-256 characters and contain no slashes.");
        return value;
    }

    public static RestartPolicy ParseRestartPolicy(string value) => value.ToLowerInvariant() switch
    {
        "on-crash" or "on-failure" => RestartPolicy.OnCrash,
        "always" => RestartPolicy.Always,
        "never" => RestartPolicy.Never,
        _ => throw new CommandLineException($"Invalid value '{value}' for --restart-policy. Expected on-crash, always or never."),
    };

    public static string FormatRestartPolicy(RestartPolicy policy) => policy switch
    {
        RestartPolicy.Always => "always",
        RestartPolicy.Never => "never",
        _ => "on-crash",
    };

    private static int ParseInt(string name, string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
            throw new CommandLineException($"Invalid value '{value}' for {name}. Expected a whole number from {min} to {max}.");
        return n;
    }

    /// <summary>Parses "90", "90s", "500ms", "2m" or "1h". A bare number is seconds.</summary>
    public static TimeSpan ParseDuration(string name, string value, bool allowZero)
    {
        var text = value.Trim().ToLowerInvariant();
        (string digits, double scaleMs) = text switch
        {
            _ when text.EndsWith("ms", StringComparison.Ordinal) => (text[..^2], 1d),
            _ when text.EndsWith('s') => (text[..^1], 1000d),
            _ when text.EndsWith('m') => (text[..^1], 60_000d),
            _ when text.EndsWith('h') => (text[..^1], 3_600_000d),
            _ => (text, 1000d),
        };
        if (!double.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
            || double.IsNaN(n) || n < 0 || n * scaleMs > TimeSpan.FromDays(7).TotalMilliseconds
            || (!allowZero && n == 0))
        {
            throw new CommandLineException(
                $"Invalid value '{value}' for {name}. Expected a duration such as 90s, 500ms, 2m or 1h{(allowZero ? " (0 disables)" : "")}.");
        }
        return TimeSpan.FromMilliseconds(n * scaleMs);
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalMilliseconds % 1000 == 0
            ? ((long)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s"
            : ((long)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";

    public static void PrintHelp(TextWriter output)
    {
        output.WriteLine("RedisService - runs redis-server as a supervised Windows service");
        output.WriteLine();
        output.WriteLine("Usage: RedisService.exe [install | uninstall | run] [options]");
        output.WriteLine();
        output.WriteLine("With no arguments (for example a double-click), Redis runs in this window using the");
        output.WriteLine("redis.conf next to RedisService.exe. Press Ctrl+C or close the window to stop it.");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  install     Install the Windows service (run as administrator)");
        output.WriteLine("  uninstall   Stop and remove the Windows service (run as administrator)");
        output.WriteLine("  run         Run Redis (default). Under the Service Control Manager it runs as a service,");
        output.WriteLine("              otherwise in the console.");
        output.WriteLine();
        output.WriteLine("Options:");
        foreach (var spec in Specs)
        {
            var names = string.Join(", ", spec.Names) + (spec.TakesValue ? $" <{spec.ValueName}>" : "");
            output.WriteLine($"  {names,-34} {spec.Help}");
        }
        output.WriteLine($"  {"-h, --help",-34} Show this help");
        output.WriteLine($"  {"-v, --version",-34} Show the version");
        output.WriteLine();
        output.WriteLine("Exit codes: 0 success, 1 failure, 2 invalid command line, 1067 redis-server kept crashing.");
        output.WriteLine();
        output.WriteLine("Examples:");
        output.WriteLine("  RedisService.exe install -c C:\\Redis\\redis.conf --port 6380");
        output.WriteLine("  RedisService.exe install --virtual-account --delayed-start");
        output.WriteLine("  RedisService.exe run --foreground");
        output.WriteLine("  RedisService.exe uninstall --service-name MyRedis");
    }

    public static void PrintVersion(TextWriter output)
    {
        var version = typeof(CommandLineParser).Assembly.GetName().Version;
        output.WriteLine($"RedisService version {version?.ToString(3) ?? "unknown"}");
        output.WriteLine("Redis Windows service wrapper");
    }
}
