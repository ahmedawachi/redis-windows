using System.Diagnostics;
using System.Net.Sockets;

namespace RedisService.Service;

public enum ProbeStatus
{
    /// <summary>+PONG.</summary>
    Ready,

    /// <summary>The event loop answered but is not serving yet (LOADING, BUSY, MASTERDOWN, NOAUTH...). Alive.</summary>
    Alive,

    /// <summary>No reply within the timeout, or no connection. Counts as a missed probe.</summary>
    Unresponsive,
}

/// <summary>Why an alive server still cannot be controlled by the wrapper.</summary>
public enum ProbeIssue
{
    None,

    /// <summary>The server rejected or required credentials (WRONGPASS, NOAUTH, NOPERM): SHUTDOWN will be refused.</summary>
    Credentials,

    /// <summary>TCP was accepted but the TLS handshake failed: no command, including SHUTDOWN, can be sent.</summary>
    Tls,
}

public sealed record ProbeResult(ProbeStatus Status, string Detail, ProbeIssue Issue = ProbeIssue.None);

public enum ShutdownOutcome
{
    /// <summary>SHUTDOWN was accepted (the server closed the connection).</summary>
    Accepted,

    /// <summary>SHUTDOWN failed and FORCE was sent and accepted; the final save did not happen.</summary>
    AcceptedWithForce,

    /// <summary>The server refused SHUTDOWN (e.g. the final save failed and FORCE is not configured).</summary>
    Refused,

    /// <summary>The wrapper could not reach or authenticate to the server.</summary>
    Unreachable,
}

/// <param name="ErrorCode">First word of the refusal (NOAUTH, WRONGPASS, ERR, ...), or empty.</param>
public sealed record ShutdownResult(ShutdownOutcome Outcome, string Detail, string ErrorCode = "")
{
    /// <summary>The refusal is about credentials, not about the final save.</summary>
    public bool IsCredentialsProblem => ErrorCode is "NOAUTH" or "WRONGPASS" or "NOPERM";

    /// <summary>The refusal is Redis's "the final save failed" error, the only one SHUTDOWN FORCE is meant for.</summary>
    public bool IsSaveFailure { get; init; }
}

/// <summary>PING and SHUTDOWN over the wrapper's own RESP connection.</summary>
public sealed class RedisControl(RedisEndpoint endpoint)
{
    public RedisEndpoint Endpoint { get; } = endpoint;

    /// <summary>
    /// Liveness probe. Any reply proves the event loop is running, so error replies (LOADING, BUSY, MASTERDOWN,
    /// and also NOAUTH/WRONGPASS from a credentials mistake) count as alive, and so does a TLS handshake the server
    /// took part in but that failed; only silence counts as a miss.
    /// </summary>
    public async Task<ProbeResult> PingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await using var connection = await RespConnection.ConnectAsync(Endpoint, timeout, cts.Token).ConfigureAwait(false);
            var reply = await connection.ExecuteAsync(["PING"], cts.Token).ConfigureAwait(false);
            if (reply is { Type: RespType.SimpleString, Text: "PONG" }) return new ProbeResult(ProbeStatus.Ready, "PONG");
            // NOAUTH here means the server wants a password the wrapper does not have (e.g. set by an ACL 'user' line).
            var issue = reply.ErrorCode is "NOAUTH" or "NOPERM" ? ProbeIssue.Credentials : ProbeIssue.None;
            return new ProbeResult(ProbeStatus.Alive, reply.ToString(), issue);
        }
        catch (RespAuthenticationException ex)
        {
            return new ProbeResult(ProbeStatus.Alive, "authentication failed: " + ex.ServerMessage, ProbeIssue.Credentials);
        }
        catch (RespTlsException ex)
        {
            return new ProbeResult(ProbeStatus.Alive, ex.Message, ProbeIssue.Tls);
        }
        catch (IOException ex) when (Endpoint.UseTls && !cts.IsCancellationRequested)
        {
            // Under TLS 1.3 the client finishes its handshake before the server checks the client certificate,
            // so a rejected certificate shows up as the server closing the session on the first read.
            return new ProbeResult(ProbeStatus.Alive, "TLS session closed by the server (it may have rejected the wrapper's client certificate): " + ex.Message, ProbeIssue.Tls);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProbeResult(ProbeStatus.Unresponsive, $"no reply within {timeout.TotalSeconds:0.#} s");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return new ProbeResult(ProbeStatus.Unresponsive, ex.Message);
        }
    }

    /// <summary>
    /// Sends AUTH (if configured) then SHUTDOWN. A connection closed without a reply means Redis accepted it.
    /// "-ERR Errors trying to SHUTDOWN" (the final save failed) is followed by SHUTDOWN FORCE only when
    /// <paramref name="forceOnError"/> is set, because FORCE discards everything written since the last save.
    /// Any other refusal (NOAUTH, a renamed command, ...) is returned as is: FORCE would not fix it.
    /// </summary>
    public async Task<ShutdownResult> ShutdownAsync(bool forceOnError, TimeSpan replyTimeout, CancellationToken cancellationToken)
    {
        RespConnection connection;
        try
        {
            connection = await RespConnection.ConnectAsync(Endpoint, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (RespAuthenticationException ex)
        {
            return new ShutdownResult(ShutdownOutcome.Unreachable, ex.Message, ex.ServerMessage.Split(' ', 2)[0]);
        }
        catch (Exception ex) when (ex is RespTlsException or IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return new ShutdownResult(ShutdownOutcome.Unreachable, $"cannot connect to {Endpoint}: {ex.Message}");
        }

        await using (connection.ConfigureAwait(false))
        {
            var budget = Stopwatch.StartNew();
            if (Endpoint.UseTls)
            {
                // A closed connection means "SHUTDOWN accepted", but a TLS session the server rejected after the
                // handshake closes the same way. Prove the session works first.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(replyTimeout);
                try
                {
                    await connection.ExecuteAsync(["PING"], cts.Token).ConfigureAwait(false);
                }
                catch (IOException ex) when (!cts.IsCancellationRequested)
                {
                    return new ShutdownResult(ShutdownOutcome.Unreachable, $"TLS session to {Endpoint} closed by the server before SHUTDOWN could be sent: {ex.Message}");
                }
            }

            var remaining = replyTimeout - budget.Elapsed;
            if (remaining < TimeSpan.FromSeconds(1)) remaining = TimeSpan.FromSeconds(1);
            var first = await SendShutdownAsync(connection, ["SHUTDOWN"], remaining, cancellationToken).ConfigureAwait(false);
            if (first is null) return new ShutdownResult(ShutdownOutcome.Accepted, "SHUTDOWN accepted");
            var saveFailed = IsSaveFailure(first);
            if (!forceOnError || !saveFailed)
                return new ShutdownResult(ShutdownOutcome.Refused, first.ToString(), first.ErrorCode) { IsSaveFailure = saveFailed };

            var forced = await SendShutdownAsync(connection, ["SHUTDOWN", "FORCE"], remaining, cancellationToken).ConfigureAwait(false);
            return forced is null
                ? new ShutdownResult(ShutdownOutcome.AcceptedWithForce, $"SHUTDOWN failed ({first}); SHUTDOWN FORCE accepted") { IsSaveFailure = true }
                : new ShutdownResult(ShutdownOutcome.Refused, $"SHUTDOWN failed ({first}); SHUTDOWN FORCE failed ({forced})", forced.ErrorCode) { IsSaveFailure = true };
        }
    }

    /// <summary>Redis's reply when the final save (or AOF flush) failed: "-ERR Errors trying to SHUTDOWN. Check logs." (blocked.c).</summary>
    public static bool IsSaveFailure(RespReply reply) =>
        reply is { IsError: true, Text: { } text } && text.StartsWith("ERR Errors trying to SHUTDOWN", StringComparison.Ordinal);

    /// <summary>Returns null when the server closed the connection (accepted), otherwise the reply it sent.</summary>
    private static async Task<RespReply?> SendShutdownAsync(RespConnection connection, string[] command, TimeSpan replyTimeout, CancellationToken cancellationToken)
    {
        // The reply only arrives once the final save is done, which can take long on a big dataset,
        // so the timeout here is the full stop budget rather than a short network timeout.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(replyTimeout);
        try
        {
            return await connection.ExecuteAsync(command, cts.Token).ConfigureAwait(false);
        }
        catch (RespConnectionClosedException)
        {
            return null;
        }
        catch (IOException) when (!cts.IsCancellationRequested)
        {
            return null; // Reset by the exiting server: accepted.
        }
    }
}
