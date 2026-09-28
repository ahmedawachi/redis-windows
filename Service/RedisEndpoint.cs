using System.Globalization;

namespace RedisService.Service;

/// <summary>TLS material for the wrapper's own connection, as Windows paths.</summary>
public sealed record RedisTlsSettings(
    string? CertFile,
    string? KeyFile,
    string? KeyFilePassword,
    string? CaCertFile,
    bool ClientCertificateRequired);

/// <summary>How the wrapper reaches its own redis-server for PING and SHUTDOWN.</summary>
public sealed record RedisEndpoint(string Host, int Port, RedisTlsSettings? Tls, string? User, string? Password)
{
    public bool UseTls => Tls is not null;

    public override string ToString() =>
        (Host.Contains(':') ? $"[{Host}]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture) + (UseTls ? " (TLS)" : "");

    // Keep the password out of any accidental logging of the record.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Endpoint = ").Append(ToString());
        return true;
    }

    /// <summary>
    /// Derives the endpoint from the effective config. Returns null when Redis has no TCP listener
    /// (port 0 and tls-port 0, e.g. unixsocket only), in which case probing and graceful shutdown are unavailable.
    /// Plain TCP is preferred when both port and tls-port are open.
    /// </summary>
    public static RedisEndpoint? FromConfig(RedisConfFile conf, ConfPathContext paths, string? password, string? user)
    {
        var port = ParsePort(conf.Get("port"), 6379);
        var tlsPort = ParsePort(conf.Get("tls-port"), 0);
        var host = ConnectHost(conf.GetArgs("bind"));

        if (port > 0)
            return new RedisEndpoint(host, port, null, user, password);
        if (tlsPort <= 0)
            return null;

        string? File(string name)
        {
            var v = conf.Get(name);
            return string.IsNullOrEmpty(v) ? null : paths.ToWindows(v, conf.EffectiveDirectoryForIncludes(paths));
        }

        var authClients = (conf.Get("tls-auth-clients") ?? "yes").ToLowerInvariant();
        var tls = new RedisTlsSettings(
            CertFile: File("tls-client-cert-file") ?? File("tls-cert-file"),
            KeyFile: File("tls-client-key-file") ?? File("tls-key-file"),
            KeyFilePassword: conf.Get("tls-client-key-file-pass") ?? conf.Get("tls-key-file-pass"),
            CaCertFile: File("tls-ca-cert-file"),
            ClientCertificateRequired: authClients != "no");
        return new RedisEndpoint(host, tlsPort, tls, user, password);
    }

    private static int ParsePort(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 65535 ? p : fallback;

    /// <summary>
    /// Picks the address to connect to from the <c>bind</c> list. The optional-address marker "-" is stripped,
    /// wildcards map to the loopback address of their family, and a loopback address is preferred when one is
    /// listed. A listed 127.x.y.z is used as is: Redis bound to 127.0.0.2 does not accept on 127.0.0.1.
    /// </summary>
    public static string ConnectHost(string[]? bind)
    {
        if (bind is null || bind.Length == 0) return "127.0.0.1"; // Redis default: bind * -::*
        var addresses = bind.Select(b => b.TrimStart('-')).Where(b => b.Length > 0).ToList();
        if (addresses.Count == 0) return "127.0.0.1";
        foreach (var a in addresses)
        {
            if (a is "*" or "0.0.0.0" || a.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return "127.0.0.1";
            if (a.StartsWith("127.", StringComparison.Ordinal)) return a;
        }
        foreach (var a in addresses)
        {
            if (a is "::*" or "::" or "::1") return "::1";
        }
        return addresses[0];
    }
}

internal static class RedisConfFileExtensions
{
    // TLS file paths are resolved by Redis relative to its working directory at load time, which is the
    // effective dir after the config has been read.
    public static string EffectiveDirectoryForIncludes(this RedisConfFile conf, ConfPathContext paths) =>
        conf.EffectiveDirectory.Length > 0 ? conf.EffectiveDirectory : paths.WorkingDirectory;
}
