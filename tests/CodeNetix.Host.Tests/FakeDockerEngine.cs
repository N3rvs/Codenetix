using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Motor Docker falso en Kestrel (127.0.0.1, puerto libre; endpoint <c>tcp://…</c>): implementa el subconjunto de la
/// API que usa el módulo Sandbox y registra cada petición con su cuerpo. Las propiedades cambian su comportamiento.
/// </summary>
internal sealed class FakeDockerEngine : IAsyncDisposable
{
    public const string ContainerId = "c0ffee0000000000000000000000000000000000000000000000000000000000";

    public const string ExecId = "e0e0e0";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Request> _requests = new();
    private volatile bool _running;

    private FakeDockerEngine()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Run(HandleAsync);
    }

    public string Endpoint { get; private set; } = string.Empty;

    public string ApiVersion { get; set; } = "1.47";

    public string MinApiVersion { get; set; } = "1.24";

    public bool ImageExists { get; set; } = true;

    public bool NetworkExists { get; set; }

    public bool NetworkInternal { get; set; } = true;

    public string NetworkGateway { get; set; } = "172.28.0.1";

    public string ContainerIp { get; set; } = "172.28.0.2";

    /// <summary>Código y mensaje de <c>POST /containers/create</c> si no es 201.</summary>
    public (int Status, string Message)? CreateContainerError { get; set; }

    /// <summary>Si es true, todo lo que no sea crear devuelve 500 (para probar que la liberación no lanza).</summary>
    public bool FailEverything { get; set; }

    /// <summary>Tramas del flujo multiplexado de <c>/exec/{id}/start</c>: (tipo, datos).</summary>
    public List<(byte Stream, byte[] Data)> ExecFrames { get; } = [];

    public int ExecExitCode { get; set; }

    public IReadOnlyList<Request> Requests => [.. _requests];

    public static async Task<FakeDockerEngine> StartAsync()
    {
        var engine = new FakeDockerEngine();
        await engine._app.StartAsync();
        var address = engine._app.Urls.First();
        engine.Endpoint = "tcp://" + new Uri(address).Authority;
        return engine;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        var request = new Request(context.Request.Method, context.Request.Path.Value ?? string.Empty, context.Request.QueryString.Value ?? string.Empty, buffer.ToArray());
        _requests.Enqueue(request);

        var path = request.Path;
        if (path == "/_ping")
        {
            await context.Response.WriteAsync("OK");
            return;
        }

        if (path == "/version")
        {
            await WriteJsonAsync(context, new { ApiVersion, MinAPIVersion = MinApiVersion, Version = "fake" });
            return;
        }

        if (!path.StartsWith("/v1.", StringComparison.Ordinal))
        {
            context.Response.StatusCode = 404;
            return;
        }

        path = path[path.IndexOf('/', 1)..];
        if (FailEverything && !(request.Method == "POST" && path == "/containers/create"))
        {
            await ErrorAsync(context, 500, "fallo simulado");
            return;
        }

        switch (request.Method, path)
        {
            case ("GET", _) when path.StartsWith("/images/", StringComparison.Ordinal):
                if (ImageExists)
                {
                    await WriteJsonAsync(context, new { Id = "sha256:fake" });
                }
                else
                {
                    await ErrorAsync(context, 404, "No such image");
                }

                return;

            case ("GET", _) when path.StartsWith("/networks/", StringComparison.Ordinal):
                if (NetworkExists)
                {
                    await WriteJsonAsync(context, new
                    {
                        Name = Uri.UnescapeDataString(path["/networks/".Length..]),
                        Internal = NetworkInternal,
                        IPAM = new
                        {
                            Config = new[] { new { Gateway = NetworkGateway } },
                        },
                    });
                }
                else
                {
                    await ErrorAsync(context, 404, "network not found");
                }

                return;

            case ("POST", "/networks/create"):
                NetworkExists = true;
                context.Response.StatusCode = 201;
                await WriteJsonAsync(context, new { Id = "net000", Warning = string.Empty });
                return;

            case ("POST", _) when path.StartsWith("/networks/", StringComparison.Ordinal) && path.EndsWith("/connect", StringComparison.Ordinal):
                context.Response.StatusCode = 200;
                return;

            case ("DELETE", _) when path.StartsWith("/networks/", StringComparison.Ordinal):
                NetworkExists = false;
                context.Response.StatusCode = 204;
                return;

            case ("POST", "/volumes/create"):
                context.Response.StatusCode = 201;
                await WriteJsonAsync(context, new { Name = "v" });
                return;

            case ("DELETE", _) when path.StartsWith("/volumes/", StringComparison.Ordinal):
                context.Response.StatusCode = 404;
                await WriteJsonAsync(context, new { message = "no such volume" });
                return;

            case ("POST", "/containers/create"):
                if (CreateContainerError is { } error)
                {
                    await ErrorAsync(context, error.Status, error.Message);
                    return;
                }

                context.Response.StatusCode = 201;
                await WriteJsonAsync(context, new { Id = ContainerId, Warnings = Array.Empty<string>() });
                return;

            case ("GET", _) when path.StartsWith("/containers/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal):
                if (!path.Contains(ContainerId, StringComparison.Ordinal))
                {
                    await ErrorAsync(context, 404, "No such container");
                    return;
                }

                await WriteJsonAsync(context, new
                {
                    Id = ContainerId,
                    State = new { Running = _running },
                    NetworkSettings = new
                    {
                        Networks = new Dictionary<string, object>
                        {
                            ["codenetix-sandbox"] = new { IPAddress = ContainerIp },
                        },
                    },
                });
                return;

            case ("PUT", _) when path.EndsWith("/archive", StringComparison.Ordinal):
                context.Response.StatusCode = 200;
                return;

            case ("POST", _) when path.EndsWith("/start", StringComparison.Ordinal) && path.StartsWith("/containers/", StringComparison.Ordinal):
                _running = true;
                context.Response.StatusCode = 204;
                return;

            case ("POST", _) when path.EndsWith("/stop", StringComparison.Ordinal):
                _running = false;
                context.Response.StatusCode = 204;
                return;

            case ("POST", _) when path.EndsWith("/kill", StringComparison.Ordinal):
                _running = false;
                context.Response.StatusCode = 204;
                return;

            case ("DELETE", _) when path.StartsWith("/containers/", StringComparison.Ordinal):
                context.Response.StatusCode = 204;
                return;

            case ("POST", _) when path.EndsWith("/exec", StringComparison.Ordinal):
                context.Response.StatusCode = 201;
                await WriteJsonAsync(context, new { Id = ExecId });
                return;

            case ("POST", _) when path == $"/exec/{ExecId}/start":
                context.Response.ContentType = "application/vnd.docker.multiplexed-stream";
                foreach (var (stream, data) in ExecFrames)
                {
                    var header = new byte[8];
                    header[0] = stream;
                    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)data.Length);
                    await context.Response.Body.WriteAsync(header);
                    await context.Response.Body.WriteAsync(data);
                    await context.Response.Body.FlushAsync();
                }

                return;

            case ("GET", _) when path == $"/exec/{ExecId}/json":
                await WriteJsonAsync(context, new { Running = false, ExitCode = ExecExitCode });
                return;

            default:
                await ErrorAsync(context, 404, $"ruta no simulada: {request.Method} {path}");
                return;
        }
    }

    private static readonly JsonSerializerOptions PascalCase = new() { PropertyNamingPolicy = null };

    /// <summary>JSON con los nombres tal cual (PascalCase, como la API de Docker).</summary>
    private static Task WriteJsonAsync(HttpContext context, object value) =>
        context.Response.WriteAsJsonAsync(value, PascalCase);

    private static async Task ErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        await WriteJsonAsync(context, new { message });
    }

    internal sealed record Request(string Method, string Path, string Query, byte[] Body)
    {
        /// <summary>Ruta sin el prefijo de versión (<c>/v1.43</c>).</summary>
        public string Route => Path.StartsWith("/v1.", StringComparison.Ordinal) ? Path[Path.IndexOf('/', 1)..] : Path;

        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();

        public string Text => Encoding.UTF8.GetString(Body);

        public override string ToString() => $"{Method} {Route}{Query}";
    }
}
