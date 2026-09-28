using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using RedisService.CommandLine;
using RedisService.Service;

namespace RedisService.Native;

/// <summary>install / uninstall: service registration, stored parameters, Event Log source and folder ACLs.</summary>
[SupportedOSPlatform("windows")]
public static class ServiceInstaller
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const string EventLogKey = @"SYSTEM\CurrentControlSet\Services\EventLog\Application";

    public static int Install(InstallOptions options, TextWriter output)
    {
        var name = options.ServiceName;
        var cwd = Environment.CurrentDirectory;
        var baseDirectory = AppContext.BaseDirectory;
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the path of RedisService.exe.");

        // Store absolute paths only: the service runs with %WINDIR%\System32 as its working directory.
        // The default config is the one next to RedisService.exe, not one in the installer's current directory.
        options.ConfigFilePath = options.ConfigFilePath is null
            ? Path.Combine(baseDirectory, "redis.conf")
            : Path.GetFullPath(options.ConfigFilePath, cwd);
        if (options.DataDirectory is not null)
        {
            options.DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DataDirectory, cwd));
            Directory.CreateDirectory(options.DataDirectory);
        }
        if (options.LogFile is not null) options.LogFile = Path.GetFullPath(options.LogFile, cwd);
        if (options.AuthPasswordFile is not null) options.AuthPasswordFile = Path.GetFullPath(options.AuthPasswordFile, cwd);
        if (options.RedisServerPath is not null) options.RedisServerPath = Path.GetFullPath(options.RedisServerPath, cwd);

        // Resolving now reports a missing config, a missing redis-server.exe or an unusable bin/usr layout before
        // anything is registered.
        var settings = SupervisorSettingsResolver.Resolve(options, baseDirectory, cwd);
        if (settings.LogFilePath is null)
        {
            options.LogFile = Path.Combine(settings.DataDirectory, "redis.log");
            output.WriteLine($"Note: the config logs to stdout (logfile \"\"), which is lost under a service. The service will use --logfile \"{options.LogFile}\".");
            settings = SupervisorSettingsResolver.Resolve(options, baseDirectory, cwd);
        }
        foreach (var warning in settings.Warnings) output.WriteLine("Warning: " + warning);

        var account = options.VirtualAccount ? $@"NT SERVICE\{name}" : null;
        var binaryPath = WindowsCommandLine.BuildMsvc(exePath, ServiceArguments.ImagePathArguments(name));
        output.WriteLine($"Installing service '{name}'...");
        ServiceManager.Install(new ServiceManager.ServiceDefinition(
            Name: name,
            DisplayName: options.DisplayName ?? (name == "Redis" ? "Redis Server" : $"Redis Server ({name})"),
            Description: options.Description ?? "Redis in-memory data store, supervised by RedisService (restarts redis-server if it fails).",
            BinaryPath: binaryPath,
            StartMode: options.StartMode,
            DelayedStart: options.DelayedStart,
            Account: account));

        try
        {
            WriteStoredArguments(name, ServiceArguments.Build(options));
            RegisterEventSource(name);
            if (account is not null) GrantAccess(name, settings, options, output);
        }
        catch
        {
            output.WriteLine("Installation failed; removing the partially installed service.");
            TryDelete(name);
            throw;
        }

        output.WriteLine($"Service '{name}' installed ({(account ?? "LocalSystem")}).");
        output.WriteLine($"  Config:   {settings.ConfigFilePath}");
        output.WriteLine($"  Data dir: {settings.DataDirectory}");
        output.WriteLine($"  Log file: {settings.LogFilePath}");
        output.WriteLine("  Recovery: restart after 5 s / 30 s / 60 s, failure count reset after 1 day.");

        if (options.StartMode == "auto")
        {
            output.WriteLine("Starting the service...");
            try
            {
                ServiceManager.Start(name);
                output.WriteLine("Service started.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                output.WriteLine($"Starting the service failed: {ex.Message}");
                output.WriteLine($"Check the Application event log (source '{name}') and start it with: sc start {name}");
                return 1;
            }
        }
        else
        {
            output.WriteLine($"Start it with: sc start {name}");
        }
        return 0;
    }

    public static int Uninstall(UninstallOptions options, TextWriter output)
    {
        var name = options.ServiceName;
        output.WriteLine($"Stopping service '{name}' (waiting up to {options.StopTimeout.TotalSeconds:0} s)...");
        if (!ServiceManager.StopAndWait(name, options.StopTimeout, output.WriteLine))
            output.WriteLine("Warning: the service did not stop in time; it will be removed once it stops.");

        ServiceManager.Delete(name);
        UnregisterEventSource(name);
        output.WriteLine($"Service '{name}' uninstalled.");
        return 0;
    }

    /// <summary>Reads the stored argument vector, or null when the service has none (installed by an older version).</summary>
    public static string[]? ReadStoredArguments(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{serviceName}\Parameters");
        return key?.GetValue(ServiceArguments.ParametersValueName) as string[];
    }

    private static void WriteStoredArguments(string serviceName, List<string> args)
    {
        using var key = Registry.LocalMachine.CreateSubKey($@"{ServicesKey}\{serviceName}\Parameters", writable: true);
        key.SetValue(ServiceArguments.ParametersValueName, args.ToArray(), RegistryValueKind.MultiString);
    }

    /// <summary>
    /// Registers the Event Log source up front: a virtual account cannot create it at run time, and the lifecycle
    /// events would be lost. The message file is the generic "%1" table shipped with .NET Framework on every
    /// supported Windows Server, so Event Viewer shows the text instead of "description not found".
    /// </summary>
    private static void RegisterEventSource(string source)
    {
        using var key = Registry.LocalMachine.CreateSubKey($@"{EventLogKey}\{source}", writable: true);
        var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll");
        var local = Path.Combine(AppContext.BaseDirectory, "System.Diagnostics.EventLog.Messages.dll");
        var messageFile = File.Exists(framework) ? framework : File.Exists(local) ? local : null;
        if (messageFile is not null) key.SetValue("EventMessageFile", messageFile, RegistryValueKind.ExpandString);
        key.SetValue("TypesSupported", 7, RegistryValueKind.DWord);
    }

    private static void UnregisterEventSource(string source)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree($@"{EventLogKey}\{source}", throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Leaving a source registration behind is harmless.
        }
    }

    /// <summary>Grants the service's virtual account what Redis needs: modify on data and log folders, read on the rest.</summary>
    private static void GrantAccess(string serviceName, SupervisorSettings settings, InstallOptions options, TextWriter output)
    {
        var account = new NTAccount("NT SERVICE", serviceName);
        var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        void GrantDirectory(string path, FileSystemRights rights)
        {
            Directory.CreateDirectory(path);
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, inherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
            output.WriteLine($"  Granted {account.Value} {rights} on {path}");
        }

        void GrantFile(string path, FileSystemRights rights)
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
            info.SetAccessControl(security);
            output.WriteLine($"  Granted {account.Value} {rights} on {path}");
        }

        output.WriteLine($"Granting folder access to {account.Value}:");
        GrantDirectory(Path.GetDirectoryName(settings.RedisServerPath)!, FileSystemRights.ReadAndExecute);
        GrantDirectory(settings.DataDirectory, FileSystemRights.Modify);
        if (settings.LogFilePath is not null)
            GrantDirectory(Path.GetDirectoryName(settings.LogFilePath)!, FileSystemRights.Modify);
        GrantFile(settings.ConfigFilePath, FileSystemRights.Read);
        if (options.AuthPasswordFile is not null)
            GrantFile(options.AuthPasswordFile, FileSystemRights.Read);
    }

    private static void TryDelete(string name)
    {
        try
        {
            ServiceManager.Delete(name);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Best effort rollback.
        }
    }
}
