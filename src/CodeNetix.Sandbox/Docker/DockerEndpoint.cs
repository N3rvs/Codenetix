using System.IO.Pipes;
using System.Net.Sockets;

namespace CodeNetix.Sandbox.Docker;

/// <summary>Transporte hacia el motor Docker: socket Unix, <i>named pipe</i> de Windows o TCP sin TLS.</summary>
internal sealed record DockerEndpoint(DockerTransport Transport, string Address)
{
    /// <summary>
    /// <c>unix:///ruta</c>, <c>npipe://./pipe/nombre</c> (o <c>npipe:////./pipe/nombre</c>) y <c>tcp://host:puerto</c>.
    /// Lanza <see cref="FormatException"/> con un motivo en español si no se reconoce.
    /// </summary>
    public static DockerEndpoint Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var raw = value.Trim();

        if (raw.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            var path = raw["unix://".Length..];
            if (path.Length == 0)
            {
                throw new FormatException($"el endpoint de Docker '{raw}' no indica la ruta del socket");
            }

            return new DockerEndpoint(DockerTransport.Unix, path);
        }

        if (raw.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase))
        {
            // npipe://./pipe/docker_engine y npipe:////./pipe/docker_engine: el nombre va tras "pipe/".
            var rest = raw["npipe://".Length..].Replace('\\', '/');
            var marker = rest.IndexOf("pipe/", StringComparison.OrdinalIgnoreCase);
            var name = marker < 0 ? string.Empty : rest[(marker + "pipe/".Length)..];
            if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal))
            {
                throw new FormatException($"el endpoint de Docker '{raw}' no es un named pipe válido (npipe://./pipe/<nombre>)");
            }

            return new DockerEndpoint(DockerTransport.NamedPipe, name);
        }

        if (raw.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var withoutScheme = raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
            if (!Uri.TryCreate("http://" + withoutScheme, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new FormatException($"el endpoint de Docker '{raw}' no es una dirección tcp://host:puerto válida");
            }

            return new DockerEndpoint(DockerTransport.Tcp, $"{uri.Host}:{uri.Port}");
        }

        throw new FormatException($"el endpoint de Docker '{raw}' no está admitido (usa unix://, npipe:// o tcp://)");
    }

    /// <summary>
    /// Como <see cref="Parse"/>, pero un valor no reconocido da un endpoint <see cref="DockerTransport.Invalid"/> cuyas
    /// conexiones fallan con el motivo: así un endpoint mal configurado lleva a <c>Diagnostic</c>, no a un arranque fallido.
    /// </summary>
    public static DockerEndpoint ParseOrInvalid(string value)
    {
        try
        {
            return Parse(value);
        }
        catch (FormatException ex)
        {
            return new DockerEndpoint(DockerTransport.Invalid, ex.Message) { Original = value };
        }
    }

    /// <summary>Texto configurado (solo en <see cref="DockerTransport.Invalid"/>).</summary>
    private string? Original { get; init; }

    /// <summary>Dirección base de las peticiones HTTP.</summary>
    public Uri BaseAddress => Transport == DockerTransport.Tcp ? new Uri($"http://{Address}/") : new Uri("http://docker/");

    /// <summary>Abre la conexión (socket Unix o named pipe); <c>null</c> en TCP (la abre el propio handler).</summary>
    public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        switch (Transport)
        {
            case DockerTransport.Unix:
                if (!File.Exists(Address))
                {
                    throw new IOException($"no existe el socket {Address}");
                }

                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(Address), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException ex)
                {
                    socket.Dispose();
                    throw new IOException($"no se puede conectar al socket {Address}: {ex.Message}", ex);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }

            case DockerTransport.NamedPipe:
                var pipe = new NamedPipeClientStream(".", Address, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

            case DockerTransport.Invalid:
                throw new IOException(Address);

            default:
                throw new InvalidOperationException("El transporte TCP no usa ConnectAsync.");
        }
    }

    public override string ToString() => Transport switch
    {
        DockerTransport.Unix => "unix://" + Address,
        DockerTransport.NamedPipe => "npipe://./pipe/" + Address,
        DockerTransport.Invalid => Original ?? string.Empty,
        _ => "tcp://" + Address,
    };
}

internal enum DockerTransport
{
    Unix,
    NamedPipe,
    Tcp,

    /// <summary>Endpoint no reconocido: cada conexión falla con el motivo.</summary>
    Invalid,
}
