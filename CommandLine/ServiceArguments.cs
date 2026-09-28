namespace RedisService.CommandLine;

/// <summary>
/// Converts install options into the argument vector stored under
/// HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;\Parameters (value "Arguments", REG_MULTI_SZ).
/// Keeping options there instead of in ImagePath removes the whole class of Windows command-line quoting bugs
/// (e.g. a --dir ending in a backslash swallowing the rest of the line).
/// </summary>
public static class ServiceArguments
{
    public const string ParametersValueName = "Arguments";

    /// <summary>The ImagePath arguments after the executable: only what is needed to find the stored parameters.</summary>
    public static IReadOnlyList<string> ImagePathArguments(string serviceName) => ["run", "--service-name", serviceName];

    /// <summary>Builds the stored argument vector. Paths must already be absolute.</summary>
    public static List<string> Build(RunOptions o)
    {
        var args = new List<string>();
        if (o.ConfigFilePath is not null) args.AddRange(["-c", o.ConfigFilePath]);
        if (o.Port is { } port) args.AddRange(["--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (o.DataDirectory is not null) args.AddRange(["--dir", o.DataDirectory]);
        if (o.LogLevel is not null) args.AddRange(["--loglevel", o.LogLevel]);
        if (o.LogFile is not null) args.AddRange(["--logfile", o.LogFile]);
        if (o.RedisServerPath is not null) args.AddRange(["--redis-server", o.RedisServerPath]);

        var defaults = new RunOptions();
        if (o.RestartPolicy != defaults.RestartPolicy) args.AddRange(["--restart-policy", CommandLineParser.FormatRestartPolicy(o.RestartPolicy)]);
        if (o.StopTimeout != defaults.StopTimeout) args.AddRange(["--stop-timeout", CommandLineParser.FormatDuration(o.StopTimeout)]);
        if (o.StartTimeout != defaults.StartTimeout) args.AddRange(["--start-timeout", CommandLineParser.FormatDuration(o.StartTimeout)]);
        if (o.HealthInterval != defaults.HealthInterval) args.AddRange(["--health-interval", CommandLineParser.FormatDuration(o.HealthInterval)]);
        if (o.HealthFailures != defaults.HealthFailures) args.AddRange(["--health-failures", Invariant(o.HealthFailures)]);
        if (o.MaxRestarts != defaults.MaxRestarts) args.AddRange(["--max-restarts", Invariant(o.MaxRestarts)]);
        if (o.RestartWindow != defaults.RestartWindow) args.AddRange(["--restart-window", CommandLineParser.FormatDuration(o.RestartWindow)]);
        if (o.AuthUser is not null) args.AddRange(["--auth-user", o.AuthUser]);
        if (o.AuthPasswordFile is not null) args.AddRange(["--auth-password-file", o.AuthPasswordFile]);
        if (o.ShutdownForce) args.Add("--shutdown-force");
        return args;
    }

    private static string Invariant(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
