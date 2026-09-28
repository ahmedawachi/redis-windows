using System.Globalization;
using RedisService.CommandLine;

namespace RedisService.Service;

/// <summary>A configuration problem that makes starting pointless (missing config file, unusable paths).</summary>
public sealed class WrapperConfigurationException(string message) : Exception(message)
{
    public const int ExitCode = 1;
}

/// <summary>Everything the supervisor needs, fully resolved from the command line and redis.conf.</summary>
public sealed record SupervisorSettings
{
    public required string RedisServerPath { get; init; }
    public required string WorkingDirectory { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required RuntimeFlavor Flavor { get; init; }
    public required string ConfigFilePath { get; init; }

    /// <summary>Host path of the directory Redis works in (the effective <c>dir</c>).</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Null when Redis has no TCP listener; probing and graceful shutdown are then unavailable.</summary>
    public RedisEndpoint? Endpoint { get; init; }

    /// <summary>Host path of the Redis logfile, or null when Redis logs to stdout.</summary>
    public string? LogFilePath { get; init; }

    public RestartPolicy RestartPolicy { get; init; } = RestartPolicy.OnCrash;
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int HealthFailures { get; init; } = 12;
    public int MaxRestarts { get; init; } = 5;
    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(10);
    public bool ShutdownForce { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Timeout of one liveness probe.</summary>
    public TimeSpan ProbeTimeout => HealthInterval > TimeSpan.Zero && HealthInterval < TimeSpan.FromSeconds(2)
        ? HealthInterval
        : TimeSpan.FromSeconds(2);

    public string DescribeCommandLine() =>
        string.Join(' ', Arguments.Prepend(RedisServerPath).Select(a => a.Length == 0 || a.Contains(' ') ? $"\"{a}\"" : a));
}

public static class SupervisorSettingsResolver
{
    /// <param name="options">Parsed options.</param>
    /// <param name="baseDirectory">Directory of RedisService.exe.</param>
    /// <param name="relativeBase">Base for relative paths given on the command line (cwd in the console, the exe directory for the service).</param>
    public static SupervisorSettings Resolve(RunOptions options, string baseDirectory, string relativeBase)
    {
        baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var redisServer = options.RedisServerPath is not null
            ? Path.GetFullPath(options.RedisServerPath, relativeBase)
            : Path.Combine(baseDirectory, OperatingSystem.IsWindows() ? "redis-server.exe" : "redis-server");
        if (!File.Exists(redisServer))
            throw new WrapperConfigurationException($"redis-server was not found at '{redisServer}'.");

        var installDirectory = Path.GetDirectoryName(redisServer)!;
        var flavor = RuntimeLayout.DetectFlavor(installDirectory);

        var configPath = options.ConfigFilePath is not null
            ? Path.GetFullPath(options.ConfigFilePath, relativeBase)
            : Path.Combine(baseDirectory, "redis.conf");
        if (!File.Exists(configPath))
            throw new WrapperConfigurationException($"Config file '{configPath}' does not exist.");

        string? dataDirectory = options.DataDirectory is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DataDirectory, relativeBase));
        string? logFileOverride = options.LogFile is null or ""
            ? options.LogFile
            : Path.GetFullPath(options.LogFile, relativeBase);

        var overrides = new List<KeyValuePair<string, string>>();
        if (options.Port is { } port) overrides.Add(new("port", port.ToString(CultureInfo.InvariantCulture)));
        if (dataDirectory is not null) overrides.Add(new("dir", dataDirectory));
        if (options.LogLevel is not null) overrides.Add(new("loglevel", options.LogLevel));
        if (logFileOverride is not null) overrides.Add(new("logfile", logFileOverride));

        // redis-server runs with the exe directory as its working directory, so that is where Redis resolves
        // relative paths in the config until a dir directive moves it.
        var paths = new ConfPathContext(baseDirectory, installDirectory, flavor);
        var conf = RedisConfFile.Load(configPath, paths, overrides);
        var warnings = new List<string>(conf.Warnings);

        var logfile = conf.Get("logfile");
        string? logFilePath = string.IsNullOrEmpty(logfile) ? null : paths.ToWindows(logfile, conf.EffectiveDirectory);

        CheckRemappedFolders(conf, logfile, logFilePath, installDirectory, flavor);

        string? password = null;
        if (options.AuthPasswordFile is not null)
        {
            var file = Path.GetFullPath(options.AuthPasswordFile, relativeBase);
            try
            {
                password = File.ReadLines(file).FirstOrDefault()?.TrimEnd('\r', '\n') ?? "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new WrapperConfigurationException($"Cannot read --auth-password-file '{file}': {ex.Message}");
            }
        }
        else if (conf.Get("requirepass") is { Length: > 0 } requirePass)
        {
            password = requirePass;
        }

        if (password is null && (conf.Contains("user") || conf.Contains("aclfile")))
        {
            warnings.Add("The config defines ACL users ('user' or 'aclfile') but gives the wrapper no password (no requirepass, no --auth-password-file). " +
                         "If the default user needs a password, health probes cannot authenticate and SHUTDOWN is refused, so stopping the service " +
                         "skips the final save. Pass --auth-user and --auth-password-file.");
        }

        var endpoint = RedisEndpoint.FromConfig(conf, paths, password, options.AuthUser);
        if (endpoint is null)
            warnings.Add("Redis has no TCP listener (port 0 and tls-port 0): health probing and graceful SHUTDOWN are unavailable; stopping will terminate the process.");
        else if (endpoint.Tls is { } tls && RespConnection.CheckClientCertificate(tls) is { } certificateProblem)
            warnings.Add($"The wrapper {certificateProblem}. Liveness falls back to TCP accept, and graceful SHUTDOWN over TLS is unavailable; stopping will terminate the process.");

        string ToArg(string hostPath) => flavor == RuntimeFlavor.Native ? hostPath : RuntimeLayout.ToRedisPath(hostPath);

        var args = new List<string> { ToArg(configPath) };
        if (options.Port is { } p) args.AddRange(["--port", p.ToString(CultureInfo.InvariantCulture)]);
        if (dataDirectory is not null) args.AddRange(["--dir", ToArg(dataDirectory)]);
        if (options.LogLevel is not null) args.AddRange(["--loglevel", options.LogLevel]);
        if (logFileOverride is not null) args.AddRange(["--logfile", logFileOverride.Length == 0 ? "" : ToArg(logFileOverride)]);

        return new SupervisorSettings
        {
            RedisServerPath = redisServer,
            WorkingDirectory = baseDirectory,
            Arguments = args,
            Flavor = flavor,
            ConfigFilePath = configPath,
            DataDirectory = conf.EffectiveDirectory,
            Endpoint = endpoint,
            LogFilePath = logFilePath,
            RestartPolicy = options.RestartPolicy,
            StopTimeout = options.StopTimeout,
            StartTimeout = options.StartTimeout,
            HealthInterval = options.HealthInterval,
            HealthFailures = options.HealthFailures,
            MaxRestarts = options.MaxRestarts,
            RestartWindow = options.RestartWindow,
            ShutdownForce = options.ShutdownForce,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Refuses layouts where Redis would silently read and write the wrong folder: under MSYS2 the runtime
    /// root is two folders above redis-server.exe and &lt;root&gt;\bin is shadowed by the /bin mount, so a package in
    /// e.g. C:\bin\Redis resolves relative paths into C:\usr\bin\Redis (upstream issues #60, #69). Absolute C:/ paths
    /// for dir and logfile bypass the mount table and are always safe. The data directory itself must not be in such
    /// a folder, because dump.rdb and the pidfile are always resolved relative to it.
    /// </summary>
    private static void CheckRemappedFolders(RedisConfFile conf, string? logfile, string? logFilePath, string installDirectory, RuntimeFlavor flavor)
    {
        if (flavor == RuntimeFlavor.Native) return;
        var folders = string.Join(", ", RuntimeLayout.RemappedFolders(installDirectory, flavor));

        if (RuntimeLayout.IsInRemappedFolder(conf.EffectiveDirectory, installDirectory, flavor))
        {
            throw new WrapperConfigurationException(
                $"The Redis data directory '{conf.EffectiveDirectory}' is inside {folders}, which the {flavor} runtime maps to a different folder, " +
                "so dump.rdb would be written somewhere else. Move the package out of folders named 'bin' or 'usr', or set 'dir' " +
                "(or --dir) to an absolute path outside them.");
        }
        if (logfile is { Length: > 0 } && RuntimeLayout.IsConfPathRelative(logfile) && logFilePath is not null
            && RuntimeLayout.IsInRemappedFolder(logFilePath, installDirectory, flavor))
        {
            throw new WrapperConfigurationException(
                $"The relative logfile '{logfile}' resolves inside {folders}, which the {flavor} runtime maps to a different folder. " +
                "Set 'logfile' (or --logfile) to an absolute path such as C:/Redis/logs/redis.log.");
        }
    }
}
