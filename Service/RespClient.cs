using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RedisService.Service;

public enum RespType
{
    SimpleString,
    Error,
    Integer,
    BulkString,
    Array,
    Null,
}

/// <summary>One RESP2 reply. Only what PING, AUTH, INFO and SHUTDOWN can return is modelled.</summary>
public sealed record RespReply(RespType Type, string? Text = null, long Integer = 0, IReadOnlyList<RespReply>? Items = null)
{
    public bool IsError => Type == RespType.Error;

    /// <summary>First word of an error reply, e.g. "NOAUTH", "LOADING", "ERR".</summary>
    public string ErrorCode => IsError && Text is not null ? Text.Split(' ', 2)[0] : "";

    public override string ToString() => Type switch
    {
        RespType.Error => "-" + Text,
        RespType.SimpleString => "+" + Text,
        RespType.Integer => ":" + Integer.ToString(CultureInfo.InvariantCulture),
        RespType.Null => "(nil)",
        RespType.Array => $"(array of {Items?.Count ?? 0})",
        _ => Text ?? "",
    };
}

/// <summary>The server closed the connection before sending a reply (expected after a successful SHUTDOWN).</summary>
public sealed class RespConnectionClosedException() : IOException("The connection was closed by the server before a reply was received.");

/// <summary>The server sent bytes that are not valid RESP.</summary>
public sealed class RespProtocolException(string message) : IOException(message);

/// <summary>
/// A deliberately small RESP2 client for the wrapper's own control connection: one command, one reply.
/// Works over any Stream so it can be tested without a server; <see cref="ConnectAsync"/> adds TCP and optional TLS.
/// </summary>
public sealed class RespConnection : IAsyncDisposable, IDisposable
{
    private const int MaxBulkLength = 16 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;

    public RespConnection(Stream stream, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
    }

    /// <summary>Opens a connection and, when the endpoint has credentials, authenticates.</summary>
    /// <exception cref="RespAuthenticationException">AUTH was rejected.</exception>
    public static async Task<RespConnection> ConnectAsync(RedisEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var client = new TcpClient(endpoint.Host.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork) { NoDelay = true };
        RespConnection? connection = null;
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, cts.Token).ConfigureAwait(false);
            Stream stream = client.GetStream();
            if (endpoint.Tls is { } tls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, chain, errors) => ValidateServer(tls, cert, errors));
                stream = ssl;
                try
                {
                    var options = new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        ClientCertificates = tls.ClientCertificateRequired ? LoadClientCertificate(tls) : null,
                    };
                    await ssl.AuthenticateAsClientAsync(options, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException or SocketException
                                               or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or ArgumentException
                                           && !cts.IsCancellationRequested)
                {
                    // TCP was accepted, so the server is running; only the TLS layer failed.
                    ssl.Dispose();
                    throw new RespTlsException(ex);
                }
            }

            connection = new RespConnection(stream, client);
            if (endpoint.Password is not null)
            {
                var auth = endpoint.User is null
                    ? await connection.ExecuteAsync(["AUTH", endpoint.Password], cts.Token).ConfigureAwait(false)
                    : await connection.ExecuteAsync(["AUTH", endpoint.User, endpoint.Password], cts.Token).ConfigureAwait(false);
                if (auth.IsError)
                    throw new RespAuthenticationException(auth.Text ?? "AUTH failed");
            }
            return connection;
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            else client.Dispose();
            throw;
        }
    }

    private static bool ValidateServer(RedisTlsSettings tls, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        // The wrapper connects to its own child over loopback, so the host name never matches; trust is anchored
        // on tls-ca-cert-file when configured. Without a CA file there is nothing to validate against.
        if (tls.CaCertFile is null || certificate is null) return true;
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(tls.CaCertFile);
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            using var leaf = new X509Certificate2(certificate);
            return chain.Build(leaf);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, X509Certificate2> ClientCertificates = new();

    /// <summary>Returns why the client certificate for <paramref name="tls"/> cannot be loaded, or null when it loads.</summary>
    public static string? CheckClientCertificate(RedisTlsSettings tls)
    {
        if (!tls.ClientCertificateRequired) return null;
        if (tls.CertFile is null || tls.KeyFile is null)
            return "has no client certificate although tls-auth-clients requires one (set tls-client-cert-file and tls-client-key-file, or tls-cert-file and tls-key-file)";
        try
        {
            LoadClientCertificate(tls);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return $"cannot load the client certificate '{tls.CertFile}' with key '{tls.KeyFile}': {ex.Message}";
        }
    }

    /// <summary>
    /// Loads the client certificate once per file version: the probe connects every few seconds, and re-importing a
    /// key each time would churn key containers on Windows.
    /// </summary>
    private static X509CertificateCollection? LoadClientCertificate(RedisTlsSettings tls)
    {
        if (tls.CertFile is null || tls.KeyFile is null) return null;
        var cacheKey = $"{tls.CertFile}|{tls.KeyFile}|{File.GetLastWriteTimeUtc(tls.CertFile).Ticks}|{File.GetLastWriteTimeUtc(tls.KeyFile).Ticks}";
        var certificate = ClientCertificates.GetOrAdd(cacheKey, _ =>
        {
            using var pem = tls.KeyFilePassword is null
                ? X509Certificate2.CreateFromPemFile(tls.CertFile, tls.KeyFile)
                : X509Certificate2.CreateFromEncryptedPemFile(tls.CertFile, tls.KeyFilePassword, tls.KeyFile);
            // SChannel (and macOS) cannot use the ephemeral key of a PEM-loaded certificate; a PKCS#12 round trip gives it a usable key.
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        });
        return [certificate];
    }

    /// <summary>Sends one command and reads its reply.</summary>
    /// <exception cref="RespConnectionClosedException">The server closed the connection first.</exception>
    public async Task<RespReply> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(Encode(args), cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    public static byte[] Encode(IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append('*').Append(args.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (var a in args)
        {
            var bytes = Encoding.UTF8.GetByteCount(a);
            sb.Append('$').Append(bytes.ToString(CultureInfo.InvariantCulture)).Append("\r\n").Append(a).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public async Task<RespReply> ReadReplyAsync(CancellationToken cancellationToken)
    {
        var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line.Length == 0) throw new RespProtocolException("Empty reply line.");
        var payload = line[1..];
        switch (line[0])
        {
            case '+': return new RespReply(RespType.SimpleString, payload);
            case '-': return new RespReply(RespType.Error, payload);
            case ':': return new RespReply(RespType.Integer, Integer: ParseLong(payload));
            case '$':
            {
                var len = ParseLong(payload);
                if (len < 0) return new RespReply(RespType.Null);
                if (len > MaxBulkLength) throw new RespProtocolException($"Bulk reply of {len} bytes exceeds the {MaxBulkLength} byte limit.");
                var data = await ReadExactAsync((int)len + 2, cancellationToken).ConfigureAwait(false);
                if (data[^2] != '\r' || data[^1] != '\n') throw new RespProtocolException("Bulk reply is not terminated by CRLF.");
                return new RespReply(RespType.BulkString, Encoding.UTF8.GetString(data, 0, (int)len));
            }
            case '*':
            {
                var count = ParseLong(payload);
                if (count < 0) return new RespReply(RespType.Null);
                if (count > 1024 * 1024) throw new RespProtocolException("Array reply is too large.");
                var items = new List<RespReply>((int)count);
                for (var i = 0; i < count; i++)
                    items.Add(await ReadReplyAsync(cancellationToken).ConfigureAwait(false));
                return new RespReply(RespType.Array, Items: items);
            }
            default:
                throw new RespProtocolException($"Unexpected reply type '{line[0]}'.");
        }
    }

    private static long ParseLong(string s) =>
        long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new RespProtocolException($"Invalid number '{s}' in reply.");

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var sb = new List<byte>();
        while (true)
        {
            if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                if (sb.Count == 0) throw new RespConnectionClosedException();
                throw new RespProtocolException("Connection closed in the middle of a reply.");
            }
            var span = _buffer.AsSpan(_start, _end - _start);
            var nl = span.IndexOf((byte)'\n');
            if (nl >= 0)
            {
                sb.AddRange(span[..nl].ToArray());
                _start += nl + 1;
                if (sb.Count > 0 && sb[^1] == '\r') sb.RemoveAt(sb.Count - 1);
                return Encoding.UTF8.GetString([.. sb]);
            }
            sb.AddRange(span.ToArray());
            _start = _end;
            if (sb.Count > 64 * 1024) throw new RespProtocolException("Reply line is too long.");
        }
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        var result = new byte[count];
        var filled = 0;
        while (filled < count)
        {
            if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                throw new RespProtocolException("Connection closed in the middle of a reply.");
            var n = Math.Min(count - filled, _end - _start);
            Buffer.BlockCopy(_buffer, _start, result, filled, n);
            _start += n;
            filled += n;
        }
        return result;
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        _start = 0;
        int read;
        try
        {
            read = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        {
            read = 0; // A reset after SHUTDOWN is the same outcome as an orderly close.
        }
        _end = read;
        return read > 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _owner?.Dispose();
    }

    public void Dispose()
    {
        _stream.Dispose();
        _owner?.Dispose();
    }
}

/// <summary>
/// The TCP connection was accepted but TLS could not be set up: the handshake was refused by either side, or the
/// client certificate could not be loaded. The server is running; the wrapper's TLS settings do not match it.
/// </summary>
public sealed class RespTlsException(Exception inner) : Exception($"TLS handshake failed: {Describe(inner)}", inner)
{
    private static string Describe(Exception ex) =>
        ex.InnerException is { Message.Length: > 0 } i && i.Message != ex.Message ? $"{ex.Message} ({i.Message})" : ex.Message;
}

/// <summary>AUTH was rejected (WRONGPASS or an ERR about AUTH).</summary>
public sealed class RespAuthenticationException(string serverMessage) : Exception($"Redis rejected the wrapper's credentials: {serverMessage}")
{
    public string ServerMessage { get; } = serverMessage;
}
