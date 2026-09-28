using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using RedisService.CommandLine;
using RedisService.Native;
using RedisService.Service;

namespace RedisService;

internal static class Program
{
    private const string LifecycleCategory = "RedisService";
    private const string OutputCategory = "RedisService.RedisOutput";

    // Kept for the life of the process: closing it would kill redis-server (KILL_ON_JOB_CLOSE).
    private static JobObject? s_wrapperJob;

    private static async Task<int> Main(string[] args)
    {
        var exitCode = await RunCommandLineAsync(args).ConfigureAwait(false);
        // A double-clicked window closes the moment the process exits, taking the error with it. Never wait where
        // nobody can answer: a scheduled task's hidden console, a CI job, redirected input.
        if (args.Length == 0 && exitCode != 0 && Environment.UserInteractive && Environment.GetEnvironmentVariable("CI") is null
            && ConsoleWindow.IsOwnedByThisProcess() && !Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.WriteLine("Redis stopped because of the error above. Press any key to close this window.");
            Console.ReadKey(intercept: true);
        }
        return exitCode;
    }

    private static async Task<int> RunCommandLineAsync(string[] args)
    {
        CommandResult command;
        try
        {
            command = CommandLineParser.Parse(args);
        }
        catch (CommandLineException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return CommandLineException.ExitCode;
        }

        try
        {
            switch (command)
            {
                case HelpCommand:
                    CommandLineParser.PrintHelp(Console.Out);
                    return 0;
                case VersionCommand:
                    CommandLineParser.PrintVersion(Console.Out);
                    return 0;
                case InstallCommand install:
                    return OperatingSystem.IsWindows() ? ServiceInstaller.Install(install.Options, Console.Out) : WindowsOnly("install");
                case UninstallCommand uninstall:
                    return OperatingSystem.IsWindows() ? ServiceInstaller.Uninstall(uninstall.Options, Console.Out) : WindowsOnly("uninstall");
                case RunCommand run:
                    var runArgs = args.Length > 0 && args[0].Equals("run", StringComparison.OrdinalIgnoreCase) ? args[1..] : args;
                    return await RunAsync(run.Options, runArgs, welcome: args.Length == 0).ConfigureAwait(false);
                default:
                    CommandLineParser.PrintHelp(Console.Out);
                    return 0;
            }
        }
        catch (CommandLineException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return CommandLineException.ExitCode;
        }
        catch (WrapperConfigurationException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return WrapperConfigurationException.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            Console.Error.WriteLine($"Error: {ex.Message} Access is denied: run this command as administrator.");
            return 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected error: {ex}");
            return 1;
        }
    }

    private static int WindowsOnly(string command)
    {
        Console.Error.WriteLine($"Error: '{command}' is only available on Windows.");
        return 1;
    }

    private static Task<int> RunAsync(RunOptions options, string[] runArgs, bool welcome)
    {
        if (OperatingSystem.IsWindows() && !options.Foreground && WindowsServiceHelpers.IsWindowsService())
            return RunAsServiceAsync(LayerStoredArguments(options, runArgs));
        return RunInForegroundAsync(options, welcome);
    }

    /// <summary>Stored install parameters first, then whatever the ImagePath adds, so an explicit flag still wins.</summary>
    [SupportedOSPlatform("windows")]
    private static RunOptions LayerStoredArguments(RunOptions options, string[] runArgs)
    {
        // Installs made by RedisService 2.x keep every option in ImagePath and pass no --service-name.
        if (!options.ServiceNameSpecified) return options;
        var stored = ServiceInstaller.ReadStoredArguments(options.ServiceName);
        if (stored is null) return options;
        var layered = CommandLineParser.ParseRun(stored);
        CommandLineParser.ApplyRun(layered, runArgs);
        return layered;
    }

    private static void AssignWrapperJob(Action<string> warn)
    {
        if (OperatingSystem.IsWindows())
            s_wrapperJob = JobObject.AssignCurrentProcess(warn);
    }

    private static async Task<int> RunInForegroundAsync(RunOptions options, bool welcome)
    {
        var warnings = new List<string>();
        AssignWrapperJob(warnings.Add);

        var settings = SupervisorSettingsResolver.Resolve(options, AppContext.BaseDirectory, Environment.CurrentDirectory);
        if (welcome) PrintWelcome(Console.Out, settings);
        using var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Debug)
            .AddProvider(new ConsoleLineLoggerProvider(Console.Out, Console.Error, LogLevel.Information)));
        var log = loggerFactory.CreateLogger(LifecycleCategory);
        foreach (var warning in warnings) log.LogWarning("{Warning}", warning);

        var supervisor = new RedisSupervisor(settings, RedisLaunchers.Create(w => log.LogWarning("{Warning}", w)), log, loggerFactory.CreateLogger(OutputCategory));
        using var stop = new CancellationTokenSource();
        var interrupts = 0;

        void RequestStop(string source)
        {
            if (Interlocked.Increment(ref interrupts) == 1)
            {
                log.LogInformation("{Source}: stopping Redis gracefully (press Ctrl+C again to force).", source);
                stop.Cancel();
            }
            else
            {
                log.LogWarning("{Source} again: terminating redis-server now.", source);
                supervisor.ForceStop();
            }
        }

        // Removed on the way out: 'stop' is disposed by then, and a Ctrl+C at the error pause must just end the process.
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            RequestStop("Ctrl+C");
        };
        Console.CancelKeyPress += onCancel;
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            RequestStop("SIGTERM");
        });

        // Closing the console window (CTRL_CLOSE_EVENT on Windows): Windows ends the process about 5 s after this
        // handler starts, and the job object then takes redis-server with it. Hold the handler while Redis stops
        // gracefully so that a small dataset is still saved. Not disposed: the handler may still be waiting on it.
        var finished = new ManualResetEventSlim();
        using var windowClosed = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
        {
            ctx.Cancel = true;
            RequestStop("Window closed");
            finished.Wait(TimeSpan.FromSeconds(4.5));
        });

        log.LogInformation("RedisService {Version} running redis-server in the foreground; press Ctrl+C to stop.",
            typeof(Program).Assembly.GetName().Version?.ToString(3));
        try
        {
            var result = await supervisor.RunAsync(stop.Token).ConfigureAwait(false);
            log.LogInformation("RedisService exiting with code {Code}: {Reason}", result.ForegroundExitCode, result.Reason);
            return result.ForegroundExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            finished.Set();
        }
    }

    /// <summary>What someone who just double-clicked RedisService.exe needs to see before the Redis log starts.</summary>
    private static void PrintWelcome(TextWriter output, SupervisorSettings settings)
    {
        foreach (var line in WelcomeLines(settings, OperatingSystem.IsWindows())) output.WriteLine(line);
    }

    internal static IReadOnlyList<string> WelcomeLines(SupervisorSettings settings, bool windows)
    {
        var folder = Path.GetDirectoryName(settings.RedisServerPath) ?? AppContext.BaseDirectory;
        var cli = "\"" + Path.Combine(folder, windows ? "redis-cli.exe" : "redis-cli") + "\"";
        var connect = settings.Endpoint is { } endpoint
            ? cli + CliArguments(endpoint)
            : "no TCP port is configured (set port in redis.conf)";
        return
        [
            windows ? "Redis for Windows is starting in this window." : "Redis is starting in this window.",
            $"  Config   {settings.ConfigFilePath}",
            $"  Data     {settings.DataDirectory}",
            $"  Connect  {connect}",
            "  Stop     press Ctrl+C, or close this window",
            "",
        ];
    }

    private static string CliArguments(RedisEndpoint endpoint)
    {
        var arguments = endpoint.Host is "127.0.0.1" or "localhost" ? "" : $" -h {endpoint.Host}";
        if (endpoint.Port != 6379) arguments += $" -p {endpoint.Port}";
        if (endpoint.UseTls) arguments += " --tls (plus your certificate options)";
        return arguments;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunAsServiceAsync(RunOptions options)
    {
        var jobWarnings = new List<string>();
        AssignWrapperJob(jobWarnings.Add);

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "RedisService",
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Services.AddWindowsService(o => o.ServiceName = options.ServiceName);
        builder.Services.AddSingleton<RedisServiceLifetime>();
        builder.Services.AddSingleton<IHostLifetime>(sp => sp.GetRequiredService<RedisServiceLifetime>());
        builder.Services.AddSingleton<IServiceExitCode>(sp => sp.GetRequiredService<RedisServiceLifetime>());

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddEventLog(new EventLogSettings { SourceName = options.ServiceName, LogName = "Application" });
        // Lifecycle events only: host chatter stays out, Redis stdout (Debug) never reaches the Event Log,
        // stderr arrives rate-limited at Warning.
        builder.Logging.AddFilter<EventLogLoggerProvider>("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter<EventLogLoggerProvider>(LifecycleCategory, LogLevel.Information);
        builder.Logging.AddFilter<EventLogLoggerProvider>(OutputCategory, LogLevel.Warning);

        // The host's own deadline must be longer than the graceful stop, or it would cut the final save short.
        builder.Services.Configure<HostOptions>(o =>
        {
            o.ShutdownTimeout = options.StopTimeout + TimeSpan.FromSeconds(15);
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<Func<SupervisorSettings, RedisSupervisor>>(sp =>
        {
            var factory = sp.GetRequiredService<ILoggerFactory>();
            var log = factory.CreateLogger(LifecycleCategory);
            foreach (var warning in jobWarnings) log.LogWarning(EventIds.ConfigWarning, "{Warning}", warning);
            return settings => new RedisSupervisor(settings, RedisLaunchers.Create(w => log.LogWarning(EventIds.ConfigWarning, "{Warning}", w)),
                log, factory.CreateLogger(OutputCategory));
        });
        builder.Services.AddHostedService<RedisHostedService>();

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return host.Services.GetRequiredService<IServiceExitCode>().ExitCode;
    }
}
