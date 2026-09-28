using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RedisService.CommandLine;
using RedisService.Service;

namespace RedisService.Tests;

/// <summary>
/// TLS-only Redis (port 0 + tls-port): the endpoint is derived from the config and the wrapper connects with the
/// configured certificate. Needs REDIS_TLS_SERVER_PATH pointing at a BUILD_TLS=yes redis-server.
/// </summary>
[Collection("real-redis")]
public class TlsTests
{
    private static string? TlsServer =>
        Environment.GetEnvironmentVariable("REDIS_TLS_SERVER_PATH") is { Length: > 0 } p && File.Exists(p) ? p : null;

    private static void WritePem(string path, X509Certificate2 cert, RSA? key = null)
    {
        File.WriteAllText(path, cert.ExportCertificatePem());
        if (key is not null) File.WriteAllText(Path.ChangeExtension(path, ".key"), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>A CA and a server certificate without EKU (usable as client certificate too, like Redis's gen-test-certs.sh).</summary>
    private static (string Ca, string Cert, string Key, string OtherCa) MakeCertificates(string dir)
    {
        var now = DateTimeOffset.UtcNow;
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=RedisService Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caReq.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.Create(ca, now.AddDays(-1), now.AddDays(1), [1, 2, 3, 4]);

        using var otherKey = RSA.Create(2048);
        using var other = new CertificateRequest("CN=Unrelated CA", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        var caPath = Path.Combine(dir, "ca.crt");
        var certPath = Path.Combine(dir, "redis.crt");
        var otherPath = Path.Combine(dir, "other-ca.crt");
        WritePem(caPath, ca);
        WritePem(certPath, cert, key);
        WritePem(otherPath, other);
        return (caPath, certPath, Path.ChangeExtension(certPath, ".key"), otherPath);
    }

    [Fact]
    public async Task TlsOnly_PingAndShutdown_WithClientCertificate()
    {
        Assert.SkipWhen(TlsServer is null, "REDIS_TLS_SERVER_PATH is not set to a TLS-enabled redis-server.");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only unit test.");
        using var dir = new TempDir();
        var (ca, cert, key, otherCa) = MakeCertificates(dir.Path);
        var port = RedisTestInstance.FreePort();
        var conf = dir.File("redis.conf", $"""
            port 0
            tls-port {port}
            bind 127.0.0.1
            tls-cert-file {cert}
            tls-key-file {key}
            tls-ca-cert-file {ca}
            requirepass tlspass
            dir {dir.Path}
            logfile {dir.Path}/redis.log
            save 3600 1
            """);

        var ctx = new ConfPathContext(dir.Path, dir.Path, RuntimeFlavor.Native);
        var parsed = RedisConfFile.Load(conf, ctx);
        var endpoint = RedisEndpoint.FromConfig(parsed, ctx, "tlspass", null)!;
        Assert.True(endpoint.UseTls);
        Assert.Equal(port, endpoint.Port);
        Assert.True(endpoint.Tls!.ClientCertificateRequired);

        var psi = new ProcessStartInfo(TlsServer!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(conf);
        using var p = Process.Start(psi)!;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            var control = new RedisControl(endpoint);
            Assert.True(await Wait.UntilAsync(() => control.PingAsync(TimeSpan.FromSeconds(1), CancellationToken.None).Result.Status == ProbeStatus.Ready,
                TimeSpan.FromSeconds(20)), "no PONG over TLS: " + (File.Exists(Path.Combine(dir.Path, "redis.log")) ? File.ReadAllText(Path.Combine(dir.Path, "redis.log")) : ""));

            // Trust is anchored on tls-ca-cert-file: a certificate from another CA is refused, but the server is
            // plainly running, so that is a TLS configuration issue, never a missed probe.
            var wrongTrust = endpoint with { Tls = endpoint.Tls with { CaCertFile = otherCa } };
            var refused = await new RedisControl(wrongTrust).PingAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(ProbeStatus.Alive, refused.Status);
            Assert.Equal(ProbeIssue.Tls, refused.Issue);
            Assert.Equal(ShutdownOutcome.Unreachable, (await new RedisControl(wrongTrust).ShutdownAsync(false, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Outcome);

            // Without a client certificate the server (tls-auth-clients yes) refuses the session; under TLS 1.3 that
            // only shows after the client's handshake, and SHUTDOWN must not read the close as "accepted".
            var noClientCert = endpoint with { Tls = endpoint.Tls with { CertFile = null } };
            var noCert = await new RedisControl(noClientCert).PingAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(ProbeStatus.Alive, noCert.Status);
            Assert.Equal(ProbeIssue.Tls, noCert.Issue);
            Assert.Equal(ShutdownOutcome.Unreachable, (await new RedisControl(noClientCert).ShutdownAsync(false, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(ProbeStatus.Ready, (await control.PingAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).Status);

            var result = await control.ShutdownAsync(false, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(ShutdownOutcome.Accepted, result.Outcome);
            await p.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("DB saved on disk", File.ReadAllText(Path.Combine(dir.Path, "redis.log")));
        }
        finally
        {
            if (!p.HasExited) p.Kill();
        }
        Assert.True(RedisTestInstance.PortIsFree(port));
    }

    private static X509Certificate2 NewCa(string cn, RSA key)
    {
        var now = DateTimeOffset.UtcNow;
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return req.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));
    }

    private static X509Certificate2 Issue(X509Certificate2 ca, string cn, RSA key, byte serial)
    {
        var now = DateTimeOffset.UtcNow;
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        return req.Create(ca, now.AddDays(-1), now.AddDays(1), [serial, 7, 7, 7]);
    }

    /// <summary>
    /// W-1: tls-ca-cert-file is the CA Redis uses to verify CLIENTS; the server's own certificate may come from a
    /// different CA. The wrapper then cannot verify the server, but Redis is healthy and must not be killed.
    /// </summary>
    [Fact]
    public async Task Supervisor_ServerCertificateFromAnotherCa_IsNotKilled_AndStopStillSaves()
    {
        Assert.SkipWhen(TlsServer is null, "REDIS_TLS_SERVER_PATH is not set to a TLS-enabled redis-server.");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only unit test.");
        using var dir = new TempDir();
        using var ca1Key = RSA.Create(2048);
        using var ca2Key = RSA.Create(2048);
        using var srvKey = RSA.Create(2048);
        using var cliKey = RSA.Create(2048);
        using var ca1 = NewCa("Server CA", ca1Key);
        using var ca2 = NewCa("Client CA", ca2Key);
        using var srv = Issue(ca1, "localhost", srvKey, 1);
        using var cli = Issue(ca2, "wrapper", cliKey, 2);
        WritePem(Path.Combine(dir.Path, "srv.crt"), srv, srvKey);
        WritePem(Path.Combine(dir.Path, "cli.crt"), cli, cliKey);
        WritePem(Path.Combine(dir.Path, "client-ca.crt"), ca2);

        var port = RedisTestInstance.FreePort();
        var logPath = Path.Combine(dir.Path, "redis.log");
        var conf = dir.File("redis.conf", $"""
            port 0
            tls-port {port}
            bind 127.0.0.1
            tls-cert-file {dir.Path}/srv.crt
            tls-key-file {dir.Path}/srv.key
            tls-ca-cert-file {dir.Path}/client-ca.crt
            tls-client-cert-file {dir.Path}/cli.crt
            tls-client-key-file {dir.Path}/cli.key
            requirepass tlspass
            dir {dir.Path}
            logfile {logPath}
            save 3600 1
            """);

        var settings = SupervisorSettingsResolver.Resolve(new RunOptions
        {
            ConfigFilePath = conf,
            RedisServerPath = TlsServer,
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(30),
            HealthInterval = TimeSpan.FromMilliseconds(300),
            HealthFailures = 3,
            MaxRestarts = 2,
        }, dir.Path, dir.Path);
        var log = new ConcurrentQueue<LogEntry>();
        var supervisor = new RedisSupervisor(settings, new ManagedProcessLauncher(),
            new ListLogger("RedisService", log), new ListLogger("RedisService.RedisOutput", log), jitter: () => 0);
        using var stop = new CancellationTokenSource();
        var ct = TestContext.Current.CancellationToken;
        var run = Task.Run(() => supervisor.RunAsync(stop.Token), ct);
        string Dump() => string.Join(" | ", log.Select(e => e.Message)) + " :: " + (File.Exists(logPath) ? File.ReadAllText(logPath) : "");
        try
        {
            Assert.True(await Wait.UntilAsync(() => supervisor.ReadyCount == 1 || run.IsCompleted, TimeSpan.FromSeconds(20)), Dump());
            var pid = supervisor.CurrentProcessId;
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
            Assert.False(run.IsCompleted, Dump());
            Assert.Equal(pid, supervisor.CurrentProcessId);
            Assert.DoesNotContain(log, e => e.EventId.Id == EventIds.Crashed.Id);
            Assert.Single(log, e => e.EventId.Id == EventIds.ConfigWarning.Id && e.Message.Contains("TLS connection"));

            await stop.CancelAsync();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(40), ct);
            Assert.True(result.StopRequested);
            // SHUTDOWN could not be sent over TLS; on Unix the fallback is SIGTERM, which still saves.
            Assert.Contains("DB saved on disk", File.ReadAllText(logPath));
        }
        finally
        {
            if (!run.IsCompleted) supervisor.ForceStop();
        }
        Assert.True(await Wait.UntilAsync(() => RedisTestInstance.PortIsFree(port), TimeSpan.FromSeconds(5)));
    }
}
