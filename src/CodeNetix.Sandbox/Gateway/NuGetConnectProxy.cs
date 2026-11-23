using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeNetix.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Sandbox.Gateway;

/// <summary>
/// Proxy HTTP <c>CONNECT</c> en <c>:8081</c> (AD-7, FR-27):
/// <list type="bullet">
/// <item>solo permite túneles <c>CONNECT &lt;host&gt;:&lt;port&gt;</c> a los destinos en <see cref="SandboxOptions.AllowedNuGetHosts"/>;</item>
/// <item>copia ambos sentidos en crudo sin inspeccionar TLS (<c>200 Connection Established</c>, o <c>502 Bad Gateway</c> si el destino no conecta);</item>
/// <item>rechaza cualquier otro destino o método con <c>403 Forbidden</c>, identifica el <c>runId</c> y emite <see cref="EgressBlocked"/>.</item>
/// </list>
/// </summary>
public sealed partial class NuGetConnectProxy(
    ISandboxGateway gateway,
    IRunEventSink eventSink,
    IOptionsMonitor<SandboxOptions> options,
    ILogger<NuGetConnectProxy> logger) : IHostedService, IAsyncDisposable, IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private static readonly TimeSpan HeaderReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UpstreamConnectTimeout = TimeSpan.FromSeconds(15);

    private static readonly byte[] EstablishedResponse = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
    private static readonly byte[] ForbiddenResponse = "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
    private static readonly byte[] BadGatewayResponse = "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();

    private readonly ConcurrentDictionary<long, Task> _activeConnections = new();
    private readonly ConcurrentDictionary<string, int> _temporaryHosts = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stoppingCts;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private long _connectionId;
    private int _disposed;

    /// <summary>Puerto TCP en el que escucha actualmente el proxy <c>CONNECT</c>.</summary>
    public int BoundPort { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        var configuredPort = options.CurrentValue.ConnectProxyPort;
        if (configuredPort is < 0 or > 65535)
        {
            configuredPort = SandboxOptions.DefaultConnectProxyPort;
        }

        _stoppingCts = new CancellationTokenSource();
        try
        {
            _listener = StartListener(configuredPort);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && configuredPort == SandboxOptions.DefaultConnectProxyPort)
        {
            // En pruebas paralelas con varios hosts en el mismo proceso, si :8081 ya está ocupado se usa un puerto efímero.
            _listener = StartListener(0);
        }

        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        options.CurrentValue.ConnectProxyPort = BoundPort;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stoppingCts.Token), CancellationToken.None);
        LogStarted(BoundPort);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is null)
        {
            return;
        }

        try
        {
            if (_stoppingCts is not null)
            {
                await _stoppingCts.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            listener.Stop();
        }
        catch (SocketException)
        {
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        var active = _activeConnections.Values.ToArray();
        if (active.Length > 0)
        {
            try
            {
                await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }
        }


    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await StopAsync(cts.Token).ConfigureAwait(false);
        _stoppingCts?.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Permite temporalmente túneles hacia <paramref name="hostPort"/> (<c>host:puerto</c>) hasta que se libere el
    /// <see cref="IDisposable"/> devuelto. Solo para la provisión de <c>codenetix-tools</c> (p. ej.
    /// <c>registry.npmjs.org:443</c>); admite aperturas anidadas (cuenta de referencias).
    /// </summary>
    public IDisposable AllowTemporaryHost(string hostPort)
    {
        if (!TryParseHostPort(hostPort, out _, out _, out var normalized))
        {
            throw new ArgumentException($"Destino no válido: '{hostPort}'.", nameof(hostPort));
        }

        var key = normalized.ToLowerInvariant();
        _temporaryHosts.AddOrUpdate(key, 1, (_, count) => count + 1);
        LogTemporaryHostAllowed(key);
        return new TemporaryHostLease(this, key);
    }

    /// <summary>¿Está <paramref name="normalizedTarget"/> abierto temporalmente?</summary>
    internal bool IsTemporarilyAllowed(string normalizedTarget) =>
        _temporaryHosts.TryGetValue(normalizedTarget.ToLowerInvariant(), out var count) && count > 0;

    private void ReleaseTemporaryHost(string key)
    {
        while (_temporaryHosts.TryGetValue(key, out var count))
        {
            if (count <= 1)
            {
                if (_temporaryHosts.TryRemove(KeyValuePair.Create(key, count)))
                {
                    LogTemporaryHostRevoked(key);
                    return;
                }
            }
            else if (_temporaryHosts.TryUpdate(key, count - 1, count))
            {
                return;
            }
        }
    }

    private sealed class TemporaryHostLease(NuGetConnectProxy owner, string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.ReleaseTemporaryHost(key);
            }
        }
    }

    internal static bool IsAllowedHost(string normalizedTarget, IList<string>? allowedHosts)
    {
        if (allowedHosts is null)
        {
            return string.Equals(normalizedTarget, SandboxOptions.DefaultNuGetHost, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var raw in allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var candidate = raw.Trim();
            if (!candidate.Contains(':', StringComparison.Ordinal))
            {
                candidate += ":443";
            }

            if (string.Equals(normalizedTarget, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryParseHostPort(string rawTarget, out string host, out int port, out string normalizedTarget)
    {
        host = string.Empty;
        port = 0;
        normalizedTarget = string.Empty;
        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            return false;
        }

        var trimmed = rawTarget.Trim();
        var colon = trimmed.LastIndexOf(':');
        if (colon <= 0 || colon == trimmed.Length - 1)
        {
            return false;
        }

        var hostPart = trimmed[..colon].Trim();
        var portPart = trimmed[(colon + 1)..].Trim();
        if (hostPart.Length == 0
            || hostPart.Contains('/', StringComparison.Ordinal)
            || hostPart.Contains(' ', StringComparison.Ordinal)
            || !int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        if (hostPart.StartsWith('[') && hostPart.EndsWith(']') && hostPart.Length > 2)
        {
            host = hostPart[1..^1];
        }
        else
        {
            host = hostPart;
        }

        normalizedTarget = $"{hostPart}:{port.ToString(CultureInfo.InvariantCulture)}";
        return true;
    }

    private static TcpListener StartListener(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        return listener;
    }

    private async Task AcceptLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && _listener is { } listener)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }

            var id = Interlocked.Increment(ref _connectionId);
            var task = HandleConnectionCoreAsync(id, client, stoppingToken);
            _activeConnections[id] = task;
        }
    }

    private async Task HandleConnectionCoreAsync(long id, TcpClient client, CancellationToken stoppingToken)
    {
        try
        {
            using (client)
            {
                await HandleClientAsync(client, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Cierre de conexión normal o por parada.
        }
        finally
        {
            _activeConnections.TryRemove(id, out _);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
        var clientStream = client.GetStream();
        await using (clientStream.ConfigureAwait(false))
        {
            var headerResult = await ReadRequestHeaderAsync(clientStream, stoppingToken).ConfigureAwait(false);
            if (headerResult is null)
            {
                return;
            }

            var (headerText, leftover) = headerResult.Value;
            if (!TryParseRequest(headerText, out var method, out var rawTarget, out var hostHeader, out var proxyAuthorization))
            {
                await clientStream.WriteAsync(ForbiddenResponse, stoppingToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await clientStream.WriteAsync(ForbiddenResponse, stoppingToken).ConfigureAwait(false);
                await clientStream.FlushAsync(stoppingToken).ConfigureAwait(false);
                var blockedTarget = ExtractNonConnectTarget(rawTarget, hostHeader);
                await RecordBlockedAsync(proxyAuthorization, remoteIp, blockedTarget, $"método HTTP '{method}' no permitido en el proxy CONNECT").ConfigureAwait(false);
                return;
            }

            if (!TryParseHostPort(rawTarget, out var targetHost, out var targetPort, out var normalizedTarget)
                || !(IsAllowedHost(normalizedTarget, options.CurrentValue.AllowedNuGetHosts) || IsTemporarilyAllowed(normalizedTarget)))
            {
                await clientStream.WriteAsync(ForbiddenResponse, stoppingToken).ConfigureAwait(false);
                await clientStream.FlushAsync(stoppingToken).ConfigureAwait(false);
                await RecordBlockedAsync(proxyAuthorization, remoteIp, rawTarget, $"destino '{rawTarget}' no está en la lista permitida de NuGet").ConfigureAwait(false);
                return;
            }

            using var upstream = new TcpClient();
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                connectCts.CancelAfter(UpstreamConnectTimeout);
                await upstream.ConnectAsync(targetHost, targetPort, connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or TimeoutException && !stoppingToken.IsCancellationRequested)
            {
                await clientStream.WriteAsync(BadGatewayResponse, stoppingToken).ConfigureAwait(false);
                await clientStream.FlushAsync(stoppingToken).ConfigureAwait(false);
                return;
            }

            await clientStream.WriteAsync(EstablishedResponse, stoppingToken).ConfigureAwait(false);
            await clientStream.FlushAsync(stoppingToken).ConfigureAwait(false);

            var upstreamStream = upstream.GetStream();
            await using (upstreamStream.ConfigureAwait(false))
            {
                if (leftover.Length > 0)
                {
                    await upstreamStream.WriteAsync(leftover, stoppingToken).ConfigureAwait(false);
                    await upstreamStream.FlushAsync(stoppingToken).ConfigureAwait(false);
                }

                var toUpstream = PumpHalfAsync(clientStream, upstreamStream, upstream.Client, stoppingToken);
                var toClient = PumpHalfAsync(upstreamStream, clientStream, client.Client, stoppingToken);
                await Task.WhenAll(toUpstream, toClient).ConfigureAwait(false);
            }
        }
    }

    private static async Task PumpHalfAsync(Stream source, Stream destination, Socket destinationSocket, CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, 16 * 1024, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                destinationSocket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task RecordBlockedAsync(string? proxyAuthorization, string? remoteIp, string target, string reason)
    {
        var runId = gateway.ResolveRunId(proxyAuthorization, remoteIp);
        if (runId is null)
        {
            LogBlockedWithoutRun(target, reason);
            return;
        }

        try
        {
            await eventSink.AppendAsync(runId.Value, null, new EgressBlocked(target, reason), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBlockedEventFailed(RunShortId.Of(runId.Value), target, ex.Message);
        }
    }

    private static async Task<(string HeaderText, byte[] Leftover)?> ReadRequestHeaderAsync(Stream stream, CancellationToken stoppingToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(HeaderReadTimeout);

        var buffer = new byte[MaxHeaderBytes];
        var total = 0;
        while (total < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, MaxHeaderBytes - total), timeoutCts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            total += read;
            var headerEnd = FindHeaderEnd(buffer.AsSpan(0, total), out var delimiterLength);
            if (headerEnd >= 0)
            {
                var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
                var bodyStart = headerEnd + delimiterLength;
                var leftover = bodyStart < total ? buffer.AsSpan(bodyStart, total - bodyStart).ToArray() : [];
                return (headerText, leftover);
            }
        }

        return null;
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> span, out int delimiterLength)
    {
        var crlf = span.IndexOf("\r\n\r\n"u8);
        if (crlf >= 0)
        {
            delimiterLength = 4;
            return crlf;
        }

        var lf = span.IndexOf("\n\n"u8);
        if (lf >= 0)
        {
            delimiterLength = 2;
            return lf;
        }

        delimiterLength = 0;
        return -1;
    }

    private static bool TryParseRequest(
        string headerText,
        out string method,
        out string target,
        out string? hostHeader,
        out string? proxyAuthorization)
    {
        method = string.Empty;
        target = string.Empty;
        hostHeader = null;
        proxyAuthorization = null;

        var lines = headerText.Split('\n');
        if (lines.Length == 0)
        {
            return false;
        }

        var requestLine = lines[0].TrimEnd('\r').Trim();
        var firstSpace = requestLine.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace <= 0)
        {
            return false;
        }

        var rest = requestLine[(firstSpace + 1)..].TrimStart();
        var secondSpace = rest.IndexOf(' ', StringComparison.Ordinal);
        if (secondSpace <= 0)
        {
            return false;
        }

        method = requestLine[..firstSpace];
        target = rest[..secondSpace];

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = line.AsSpan(0, colon).Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hostHeader = value;
            }
            else if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                proxyAuthorization = value;
            }
        }

        return method.Length > 0 && target.Length > 0;
    }

    private static string ExtractNonConnectTarget(string rawTarget, string? hostHeader)
    {
        if (Uri.TryCreate(rawTarget, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        }

        if (!string.IsNullOrWhiteSpace(hostHeader))
        {
            return hostHeader.Trim();
        }

        return rawTarget;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Proxy CONNECT de NuGet escuchando en el puerto {Port}.")]
    private partial void LogStarted(int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Proxy CONNECT: '{Target}' permitido temporalmente (provisión de herramientas).")]
    private partial void LogTemporaryHostAllowed(string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Proxy CONNECT: permiso temporal de '{Target}' revocado.")]
    private partial void LogTemporaryHostRevoked(string target);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Salida bloqueada en el proxy CONNECT hacia '{Target}' ({Reason}) sin sesión activa asociada.")]
    private partial void LogBlockedWithoutRun(string target, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido registrar EgressBlocked de la Ejecución {ShortId} hacia '{Target}': {Reason}.")]
    private partial void LogBlockedEventFailed(string shortId, string target, string reason);
}
