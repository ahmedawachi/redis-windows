using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RedisService.CommandLine;
using RedisService.Service;

namespace RedisService.Tests;

/// <summary>
/// A scripted in-process server: each received command (space-joined) is answered with the raw RESP the script
/// returns, or the connection is closed when it returns null. Other modes drop every connection at once, never
/// accept (the kernel backlog still completes TCP), or complete a TLS handshake and then close the session.
/// </summary>
internal sealed class FakeRespServer : IAsyncDisposable
{
    private enum Mode { Script, DropConnections, NeverAccept, TlsThenClose }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, string?> _script;
    private readonly Mode _mode;
    private readonly X509Certificate2? _certificate;
    private readonly Task _loop;

    private FakeRespServer(Mode mode, Func<string, string?>? script = null, X509Certificate2? certificate = null)
    {
        _mode = mode;
        _script = script ?? (_ => null);
        _certificate = certificate;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = mode == Mode.NeverAccept ? Task.CompletedTask : Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public ConcurrentQueue<string> Commands { get; } = new();

    public static FakeRespServer Scripted(Func<string, string?> script) => new(Mode.Script, script);
    public static FakeRespServer DroppingConnections() => new(Mode.DropConnections);
    public static FakeRespServer NeverAccepting() => new(Mode.NeverAccept);
    public static FakeRespServer TlsThenClose() => new(Mode.TlsThenClose, certificate: SelfSigned());

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        // An ephemeral key cannot serve a TLS handshake on macOS or Windows; a PKCS#12 round trip gives it a usable one.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                switch (_mode)
                {
                    case Mode.DropConnections:
                        return;
                    case Mode.TlsThenClose:
                    {
                        await using var ssl = new SslStream(client.GetStream());
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _cts.Token);
                        return; // Handshake done, then the session is closed without a reply.
                    }
                }

                var stream = client.GetStream();
                var connection = new RespConnection(stream);
                while (true)
                {
                    var command = await connection.ReadReplyAsync(_cts.Token);
                    var text = string.Join(' ', command.Items?.Select(i => i.Text) ?? []);
                    Commands.Enqueue(text);
                    var reply = _script(text);
                    if (reply is null) return;
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), _cts.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
            {
                // The client went away.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        await _loop;
        _certificate?.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// W-1 / W-2 / W-4: an alive server the wrapper cannot control (credentials, TLS) is never treated as a hang, and
/// SHUTDOWN FORCE is reserved for Redis's "final save failed" error.
/// </summary>
public class ProbeIssueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RedisEndpoint Plain(int port, string? password = null) => new("127.0.0.1", port, null, null, password);

    private static RedisEndpoint Tls(int port, RedisTlsSettings? tls = null) =>
        new("127.0.0.1", port, tls ?? new RedisTlsSettings(null, null, null, null, false), null, null);

    [Fact]
    public async Task Ping_Noauth_IsAliveWithACredentialsIssue()
    {
        await using var server = FakeRespServer.Scripted(_ => "-NOAUTH Authentication required.\r\n");
        var probe = await new RedisControl(Plain(server.Port)).PingAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(ProbeStatus.Alive, probe.Status);
        Assert.Equal(ProbeIssue.Credentials, probe.Issue);
    }

    [Fact]
    public async Task Ping_Loading_IsAliveWithoutAnIssue()
    {
        await using var server = FakeRespServer.Scripted(_ => "-LOADING Redis is loading the dataset in memory\r\n");
        var probe = await new RedisControl(Plain(server.Port)).PingAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(ProbeStatus.Alive, probe.Status);
        Assert.Equal(ProbeIssue.None, probe.Issue);
    }

    [Fact]
    public async Task Ping_WrongPassword_IsAliveWithACredentialsIssue()
    {
        await using var server = FakeRespServer.Scripted(_ => "-WRONGPASS invalid username-password pair or user is disabled.\r\n");
        var probe = await new RedisControl(Plain(server.Port, "nope")).PingAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(ProbeStatus.Alive, probe.Status);
        Assert.Equal(ProbeIssue.Credentials, probe.Issue);
        Assert.Equal(["AUTH nope"], server.Commands);
    }

    [Fact]
    public async Task Shutdown_UnrelatedError_IsRefused_WithoutForce()
    {
        await using var server = FakeRespServer.Scripted(_ => "-ERR unknown command 'SHUTDOWN', with args beginning with: \r\n");
        var result = await new RedisControl(Plain(server.Port)).ShutdownAsync(forceOnError: true, TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ShutdownOutcome.Refused, result.Outcome);
        Assert.Equal("ERR", result.ErrorCode);
        Assert.False(result.IsSaveFailure);
        Assert.False(result.IsCredentialsProblem);
        Assert.Equal(["SHUTDOWN"], server.Commands);
    }

    [Fact]
    public async Task Shutdown_Noauth_IsACredentialsRefusal_WithoutForce()
    {
        await using var server = FakeRespServer.Scripted(_ => "-NOAUTH Authentication required.\r\n");
        var result = await new RedisControl(Plain(server.Port)).ShutdownAsync(forceOnError: true, TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ShutdownOutcome.Refused, result.Outcome);
        Assert.True(result.IsCredentialsProblem);
        Assert.False(result.IsSaveFailure);
        Assert.Equal(["SHUTDOWN"], server.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_SaveFailure_SendsForce_OnlyWhenConfigured(bool force)
    {
        await using var server = FakeRespServer.Scripted(cmd => cmd == "SHUTDOWN" ? "-ERR Errors trying to SHUTDOWN. Check logs.\r\n" : null);
        var result = await new RedisControl(Plain(server.Port)).ShutdownAsync(force, TimeSpan.FromSeconds(5), Ct);
        Assert.True(result.IsSaveFailure);
        if (force)
        {
            Assert.Equal(ShutdownOutcome.AcceptedWithForce, result.Outcome);
            Assert.Equal(["SHUTDOWN", "SHUTDOWN FORCE"], server.Commands);
        }
        else
        {
            Assert.Equal(ShutdownOutcome.Refused, result.Outcome);
            Assert.Equal(["SHUTDOWN"], server.Commands);
        }
    }

    [Fact]
    public async Task Tls_HandshakeRefusedByTheServer_IsAliveWithATlsIssue()
    {
        await using var server = FakeRespServer.DroppingConnections();
        var probe = await new RedisControl(Tls(server.Port)).PingAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(ProbeStatus.Alive, probe.Status);
        Assert.Equal(ProbeIssue.Tls, probe.Issue);
        Assert.StartsWith("TLS handshake failed", probe.Detail);
    }

    [Fact]
    public async Task Plain_ConnectionDroppedWithoutReply_IsStillAMiss()
    {
        await using var server = FakeRespServer.DroppingConnections();
        var probe = await new RedisControl(Plain(server.Port)).PingAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(ProbeStatus.Unresponsive, probe.Status);
    }

    [Fact]
    public async Task Tls_HandshakeNeverAnswered_IsUnresponsive()
    {
        // A hung Redis: TCP completes in the kernel backlog, nobody runs the handshake.
        await using var server = FakeRespServer.NeverAccepting();
        var probe = await new RedisControl(Tls(server.Port)).PingAsync(TimeSpan.FromMilliseconds(500), Ct);
        Assert.Equal(ProbeStatus.Unresponsive, probe.Status);
        Assert.Equal(ProbeIssue.None, probe.Issue);
    }

    [Fact]
    public async Task Tls_SessionClosedAfterTheHandshake_IsAlive_AndShutdownIsNotMistakenForAccepted()
    {
        // TLS 1.3 reports a rejected client certificate after the client's handshake completed.
        await using var server = FakeRespServer.TlsThenClose();
        var probe = await new RedisControl(Tls(server.Port)).PingAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ProbeStatus.Alive, probe.Status);
        Assert.Equal(ProbeIssue.Tls, probe.Issue);

        var shutdown = await new RedisControl(Tls(server.Port)).ShutdownAsync(false, TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ShutdownOutcome.Unreachable, shutdown.Outcome);
        Assert.Contains("before SHUTDOWN could be sent", shutdown.Detail);
    }

    [Fact]
    public async Task Tls_UnloadableClientCertificate_IsAliveWithATlsIssue_AndIsReported()
    {
        using var dir = new TempDir();
        var tls = new RedisTlsSettings(Path.Combine(dir.Path, "missing.crt"), Path.Combine(dir.Path, "missing.key"), null, null, true);
        Assert.Contains("cannot load the client certificate", RespConnection.CheckClientCertificate(tls));

        await using var server = FakeRespServer.DroppingConnections();
        var probe = await new RedisControl(Tls(server.Port, tls)).PingAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(ProbeStatus.Alive, probe.Status);
        Assert.Equal(ProbeIssue.Tls, probe.Issue);
    }

    [Fact]
    public async Task Supervisor_TlsHandshakeFailure_KeepsRedisRunning_AndWarnsOnce()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as the fake redis-server.");
        using var dir = new TempDir();
        await using var server = FakeRespServer.DroppingConnections();
        var fake = dir.File("fake-redis-server", "#!/bin/sh\nexec sleep 60\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var conf = dir.File("redis.conf", $"port 0\ntls-port {server.Port}\nbind 127.0.0.1\ntls-auth-clients no\n");

        var settings = SupervisorSettingsResolver.Resolve(new RunOptions
        {
            ConfigFilePath = conf,
            RedisServerPath = fake,
            StartTimeout = TimeSpan.FromSeconds(2),
            StopTimeout = TimeSpan.FromSeconds(10),
            HealthInterval = TimeSpan.FromMilliseconds(200),
            HealthFailures = 3,
            MaxRestarts = 2,
        }, dir.Path, dir.Path);
        var log = new ConcurrentQueue<LogEntry>();
        var supervisor = new RedisSupervisor(settings, new ManagedProcessLauncher(),
            new ListLogger("RedisService", log), new ListLogger("RedisService.RedisOutput", log), jitter: () => 0);
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => supervisor.RunAsync(stop.Token), Ct);
        string Dump() => string.Join(" | ", log.Select(e => e.Message));

        Assert.True(await Wait.UntilAsync(() => supervisor.ReadyCount == 1 || run.IsCompleted, TimeSpan.FromSeconds(10)), Dump());
        var pid = supervisor.CurrentProcessId;
        // Well past the start timeout and many times the 3 x 200 ms miss budget.
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.False(run.IsCompleted, Dump());
        Assert.Equal(pid, supervisor.CurrentProcessId);
        Assert.DoesNotContain(log, e => e.EventId.Id == EventIds.Crashed.Id);
        Assert.Single(log, e => e.EventId.Id == EventIds.ConfigWarning.Id && e.Message.Contains("TLS connection"));

        await stop.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(result.StopRequested);
        Assert.Contains(log, e => e.EventId.Id == EventIds.StopProblem.Id && e.Message.Contains("Could not send SHUTDOWN"));
        Assert.Null(supervisor.CurrentProcessId);
    }
}
