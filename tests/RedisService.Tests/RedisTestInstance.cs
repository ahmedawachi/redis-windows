using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RedisService.Service;

namespace RedisService.Tests;

[CollectionDefinition("real-redis", DisableParallelization = true)]
public sealed class RealRedisCollection;

/// <summary>
/// A throw-away redis-server configuration in a temp folder. Tests that need a real server skip unless
/// REDIS_SERVER_PATH points at a redis-server binary (on macOS/Linux, built from the Redis sources).
/// </summary>
internal sealed class RedisTestInstance : IAsyncDisposable
{
    public const string Password = "wrapper-test-pass";
    private readonly List<Process> _processes = [];

    private RedisTestInstance(string extraConfig, bool withPassword)
    {
        Dir = new TempDir();
        Port = FreePort();
        DataDir = Path.Combine(Dir.Path, "data");
        Directory.CreateDirectory(DataDir);
        LogPath = Path.Combine(Dir.Path, "redis.log");
        ConfPath = Dir.File("redis.conf", $"""
            port {Port}
            bind 127.0.0.1
            {(withPassword ? $"requirepass {Password}" : "")}
            dir {DataDir}
            logfile {LogPath}
            loglevel notice
            daemonize no
            save ""
            appendonly no
            enable-debug-command local
            {extraConfig}
            """);
    }

    public TempDir Dir { get; }
    public int Port { get; }
    public string DataDir { get; }
    public string LogPath { get; }
    public string ConfPath { get; }

    public static string? ServerPath =>
        Environment.GetEnvironmentVariable("REDIS_SERVER_PATH") is { Length: > 0 } p && File.Exists(p) ? p : null;

    public static RedisTestInstance Create(string extraConfig = "", bool withPassword = true)
    {
        Assert.SkipWhen(ServerPath is null, "REDIS_SERVER_PATH is not set to a redis-server binary; real-server tests are skipped.");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Real-server unit tests run on macOS/Linux; Windows is covered by ci/wrapper-integration.ps1.");
        return new RedisTestInstance(extraConfig, withPassword);
    }

    public RedisEndpoint Endpoint(string? password = Password, string? user = null) => new("127.0.0.1", Port, null, user, password);

    public string Log => File.Exists(LogPath) ? File.ReadAllText(LogPath) : "";

    /// <summary>Starts redis-server directly (not through the supervisor) and waits for PONG.</summary>
    public async Task<Process> StartDirectAsync()
    {
        var psi = new ProcessStartInfo(ServerPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(ConfPath);
        var p = Process.Start(psi)!;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _processes.Add(p);
        await WaitForPongAsync();
        return p;
    }

    public async Task WaitForPongAsync(TimeSpan? timeout = null)
    {
        var control = new RedisControl(Endpoint());
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < (timeout ?? TimeSpan.FromSeconds(20)))
        {
            if ((await control.PingAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Status == ProbeStatus.Ready) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("redis-server did not answer PING");
    }

    public async Task<RespReply> CommandAsync(params string[] args)
    {
        await using var c = await RespConnection.ConnectAsync(Endpoint(), TimeSpan.FromSeconds(5), CancellationToken.None);
        return await c.ExecuteAsync(args, CancellationToken.None);
    }

    /// <summary>Sends a command without waiting for its reply (DEBUG SLEEP, DEBUG SEGFAULT).</summary>
    public async Task FireAsync(params string[] args)
    {
        var c = await RespConnection.ConnectAsync(Endpoint(), TimeSpan.FromSeconds(5), CancellationToken.None);
        _ = c.ExecuteAsync(args, CancellationToken.None).ContinueWith(_ => c.Dispose(), TaskScheduler.Default);
    }

    public static bool PortIsFree(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _processes)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { }
            p.Dispose();
        }
        // Anything the supervisor started under this config must be gone too.
        for (var i = 0; i < 50 && !PortIsFree(Port); i++) await Task.Delay(100);
        Assert.True(PortIsFree(Port), $"port {Port} is still in use after the test");
        Dir.Dispose();
    }
}

internal sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message);

/// <summary>Captures log output so tests can assert on the lifecycle events.</summary>
internal sealed class ListLogger(string category, ConcurrentQueue<LogEntry> sink) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        sink.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception)));
}

internal static class Wait
{
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
