using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Host.Features.ReceiveWebhook;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Sandbox;
using CodeNetix.Sandbox.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Matriz de la historia 2.2: proxy inverso LLM en <c>:8080/llm/{**rest}</c> (sustitución de credenciales,
/// deduplicación de <c>/v1</c>, streaming SSE sin buffering, extracción de <c>usage</c> → <see cref="TokensConsumed"/>,
/// revocación de sesión y aislamiento de puertos) y proxy <c>CONNECT</c> en <c>:8081</c> (allowlist de NuGet,
/// túnel TCP opaco, <c>502</c> en destino inalcanzable y <c>403</c> + <see cref="EgressBlocked"/> fuera de la lista).
/// </summary>
public sealed class EgressGatewayTests : IDisposable
{
    private const string RealProviderKey = "sk-ant-realproviderkey0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public EgressGatewayTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>Panel</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Proxy inverso LLM (:8080/llm/{**rest}) ---------------------------------------------

    [Fact]
    public async Task Llm_gateway_substitutes_credentials_deduplicates_v1_and_records_TokensConsumed_from_json_response()
    {
        await using var upstream = await FakeLlmServer.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """
                {
                  "id": "msg_01",
                  "type": "message",
                  "model": "claude-sonnet-4-5-20250929",
                  "usage": {
                    "input_tokens": 120,
                    "cache_creation_input_tokens": 30,
                    "cache_read_input_tokens": 10,
                    "output_tokens": 45
                  }
                }
                """);
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Providers:Anthropic:ApiKey"] = RealProviderKey,
            ["CODENETIX:Providers:Anthropic:BaseUrl"] = upstream.BaseUrl + "/v1/",
            ["CODENETIX:Agents:Coder:Provider"] = "Anthropic",
            ["CODENETIX:Agents:Coder:Model"] = "claude-sonnet-4-5",
        };

        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();

        var runId = await SeedRunAsync(factory.Services);
        var gateway = factory.Services.GetRequiredService<ISandboxGateway>();
        var session = gateway.Register(runId);

        var context = await factory.Server.SendAsync(
            c =>
            {
                c.Connection.LocalPort = ListenPorts.LlmGateway;
                c.Request.Method = HttpMethods.Post;
                c.Request.Path = "/llm/v1/messages";
                c.Request.QueryString = new QueryString("?beta=true");
                c.Request.Headers["x-api-key"] = session.Token;
                c.Request.Headers["anthropic-version"] = "2023-06-01";
                c.Request.ContentType = "application/json";
                var payload = Encoding.UTF8.GetBytes("""{"model":"claude-sonnet-4-5","messages":[{"role":"user","content":"hola"}]}""");
                c.Request.Body = new MemoryStream(payload);
                c.Request.ContentLength = payload.Length;
            },
            Ct);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using (var bodyReader = new StreamReader(context.Response.Body, Encoding.UTF8))
        {
            _ = await bodyReader.ReadToEndAsync(Ct);
        }

        var received = Assert.Single(upstream.Requests);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/v1/messages", received.Path);
        Assert.Equal("?beta=true", received.Query);
        Assert.Equal("""{"model":"claude-sonnet-4-5","messages":[{"role":"user","content":"hola"}]}""", received.Body);
        Assert.Equal(RealProviderKey, received.Headers["x-api-key"]);
        Assert.Equal($"Bearer {RealProviderKey}", received.Headers["Authorization"]);
        Assert.Equal("2023-06-01", received.Headers["anthropic-version"]);
        Assert.DoesNotContain(session.Token, received.RawHeaders, StringComparison.Ordinal);

        var reader = factory.Services.GetRequiredService<IRunReader>();
        var events = await reader.GetEventsAsync(runId, 0, Ct);
        var consumed = Assert.Single(events.OfType<TokensConsumed>());
        Assert.Equal("Coder", consumed.Role);
        Assert.Equal("Anthropic", consumed.Provider);
        Assert.Equal("claude-sonnet-4-5-20250929", consumed.Model);
        Assert.Equal(160, consumed.InputTokens); // 120 + 30 + 10
        Assert.Equal(45, consumed.OutputTokens);
    }

    [Fact]
    public async Task Llm_gateway_supports_openai_compatible_usage_and_custom_coder_provider()
    {
        // La respuesta omite "model" para verificar que TryReadRequestModelAsync extrae el modelo del cuerpo de la petición
        // sin consumir ni corromper el flujo reenviado al Proveedor.
        await using var upstream = await FakeLlmServer.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"usage":{"prompt_tokens":15,"completion_tokens":25}}""");
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Providers:GatewayCorp:ApiKey"] = RealProviderKey,
            ["CODENETIX:Providers:GatewayCorp:BaseUrl"] = upstream.BaseUrl + "/v1",
            ["CODENETIX:Agents:Coder:Provider"] = "GatewayCorp",
            ["CODENETIX:Agents:Coder:Model"] = "custom-coder",
        };

        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();

        var runId = await SeedRunAsync(factory.Services);
        var gateway = factory.Services.GetRequiredService<ISandboxGateway>();
        var session = gateway.Register(runId);

        const string requestBody = """{"model":"request-specific-model","messages":[{"role":"user","content":"ping"}]}""";
        var context = await factory.Server.SendAsync(
            c =>
            {
                c.Connection.LocalPort = ListenPorts.LlmGateway;
                c.Request.Method = HttpMethods.Post;
                c.Request.Path = "/llm/v1/chat/completions";
                c.Request.Headers.Authorization = $"Bearer {session.Token}";
                c.Request.ContentType = "application/json";
                var payload = Encoding.UTF8.GetBytes(requestBody);
                c.Request.Body = new MemoryStream(payload);
                c.Request.ContentLength = payload.Length;
            },
            Ct);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using (var bodyReader = new StreamReader(context.Response.Body, Encoding.UTF8))
        {
            _ = await bodyReader.ReadToEndAsync(Ct);
        }

        var received = Assert.Single(upstream.Requests);
        Assert.Equal("/v1/chat/completions", received.Path);
        Assert.Equal(requestBody, received.Body);
        Assert.Equal(RealProviderKey, received.Headers["x-api-key"]);
        Assert.Equal($"Bearer {RealProviderKey}", received.Headers["Authorization"]);
        Assert.DoesNotContain(session.Token, received.RawHeaders, StringComparison.Ordinal);

        var reader = factory.Services.GetRequiredService<IRunReader>();
        var events = await reader.GetEventsAsync(runId, 0, Ct);
        var consumed = Assert.Single(events.OfType<TokensConsumed>());
        Assert.Equal("Coder", consumed.Role);
        Assert.Equal("GatewayCorp", consumed.Provider);
        Assert.Equal("request-specific-model", consumed.Model);
        Assert.Equal(15, consumed.InputTokens);
        Assert.Equal(25, consumed.OutputTokens);
    }

    [Fact]
    public async Task Llm_gateway_streams_sse_chunks_without_buffering_and_records_TokensConsumed_from_sse_events()
    {
        var firstChunkSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecondChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var upstream = await FakeLlmServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";

            await context.Response.WriteAsync(
                "event: message_start\n" +
                "data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input_tokens\":200,\"cache_read_input_tokens\":50,\"output_tokens\":1}}}\n\n");
            await context.Response.Body.FlushAsync();
            firstChunkSent.TrySetResult();

            await allowSecondChunk.Task.WaitAsync(TimeSpan.FromSeconds(10), context.RequestAborted);

            await context.Response.WriteAsync(
                "event: message_delta\n" +
                "data: {\"type\":\"message_delta\",\"usage\":{\"input_tokens\":200,\"output_tokens\":85}}\n\n");
            await context.Response.Body.FlushAsync();
        });

        var sink = new CapturingRunEventSink();
        var registry = new SandboxGatewayRegistry();
        var runId = Guid.CreateVersion7();
        var session = registry.Register(runId);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHttpForwarder();
        builder.Services.AddSingleton<ISandboxGateway>(registry);
        builder.Services.AddSingleton<IRunEventSink>(sink);
        builder.Services.AddSingleton<LlmReverseProxy>();
        builder.Services.Configure<SandboxOptions>(options =>
        {
            options.Coder = new SandboxCoderEndpoint("Anthropic", upstream.BaseUrl, RealProviderKey, "claude-sonnet-4-5");
        });

        await using var app = builder.Build();
        app.MapLlmGateway();
        await app.StartAsync(Ct);

        var baseUri = new Uri(app.Urls.First());
        app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<SandboxOptions>>().CurrentValue.LlmGatewayPort = baseUri.Port;

        using var http = new HttpClient { BaseAddress = baseUri };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/llm/v1/messages")
        {
            Content = new StringContent("""{"stream":true}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", $"Bearer {session.Token}");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await firstChunkSent.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var line1 = await reader.ReadLineAsync(Ct);
        var line2 = await reader.ReadLineAsync(Ct);

        // El primer evento SSE llega al cliente ANTES de que el servidor de origen libere el segundo chunk.
        Assert.Equal("event: message_start", line1);
        Assert.Contains("\"input_tokens\":200", line2, StringComparison.Ordinal);
        Assert.False(allowSecondChunk.Task.IsCompleted);

        allowSecondChunk.TrySetResult();
        var restOfStream = await reader.ReadToEndAsync(Ct);
        Assert.Contains("\"output_tokens\":85", restOfStream, StringComparison.Ordinal);

        var (_, _, payload) = Assert.Single(sink.Events);
        var consumed = Assert.IsType<TokensConsumed>(payload);
        Assert.Equal(new TokensConsumed("Coder", "Anthropic", "claude-sonnet-4-5", 250, 85) { RunId = runId, Seq = 1, At = consumed.At }, consumed);
    }

    [Fact]
    public async Task Llm_gateway_forwards_upstream_errors_and_redacts_real_api_key_from_error_body()
    {
        await using var upstream = await FakeLlmServer.StartAsync(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync($$$"""{"error":{"message":"Cuota agotada para {{{RealProviderKey}}}"}}""");
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Providers:Anthropic:ApiKey"] = RealProviderKey,
            ["CODENETIX:Providers:Anthropic:BaseUrl"] = upstream.BaseUrl,
        };

        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();

        var gateway = factory.Services.GetRequiredService<ISandboxGateway>();
        var session = gateway.Register(Guid.CreateVersion7());

        var context = await factory.Server.SendAsync(
            c =>
            {
                c.Connection.LocalPort = ListenPorts.LlmGateway;
                c.Request.Method = HttpMethods.Post;
                c.Request.Path = "/llm/v1/messages";
                c.Request.Headers["x-api-key"] = session.Token;
            },
            Ct);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(Ct);
        Assert.DoesNotContain(RealProviderKey, body, StringComparison.Ordinal);
        Assert.Contains("…", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Llm_gateway_rejects_missing_invalid_or_revoked_tokens_with_401_without_calling_upstream()
    {
        await using var upstream = await FakeLlmServer.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Providers:Anthropic:ApiKey"] = RealProviderKey,
            ["CODENETIX:Providers:Anthropic:BaseUrl"] = upstream.BaseUrl,
        };

        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();

        var gateway = factory.Services.GetRequiredService<ISandboxGateway>();
        var runId = Guid.CreateVersion7();
        var session = gateway.Register(runId);

        // 1) Sin clave.
        var missing = await SendLlmAsync(factory, ListenPorts.LlmGateway, "/llm/v1/messages", apiKey: null);
        Assert.Equal(StatusCodes.Status401Unauthorized, missing.Response.StatusCode);

        // 2) Clave inválida.
        var invalid = await SendLlmAsync(factory, ListenPorts.LlmGateway, "/llm/v1/messages", apiKey: $"cnx-run-{runId:D}-00000000000000000000000000000000");
        Assert.Equal(StatusCodes.Status401Unauthorized, invalid.Response.StatusCode);

        // 3) Clave revocada tras finalizar la sesión.
        gateway.Revoke(runId);
        var revoked = await SendLlmAsync(factory, ListenPorts.LlmGateway, "/llm/v1/messages", apiKey: session.Token);
        Assert.Equal(StatusCodes.Status401Unauthorized, revoked.Response.StatusCode);

        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("GET", ListenPorts.LlmGateway, "/")]
    [InlineData("GET", ListenPorts.LlmGateway, "/api/health")]
    [InlineData("POST", ListenPorts.LlmGateway, "/webhooks/github")]
    [InlineData("POST", ListenPorts.LlmGateway, "/llm")]
    [InlineData("POST", ListenPorts.LlmGateway, "/llm/")]
    [InlineData("POST", ListenPorts.LlmGateway, "/llm///")]
    [InlineData("POST", ListenPorts.LlmGateway, "/llm/v1/../messages")]
    [InlineData("POST", ListenPorts.Main, "/llm/v1/messages")]
    [InlineData("POST", 0, "/llm/v1/messages")]
    [InlineData("POST", ListenPorts.Webhooks, "/llm/v1/messages")]
    public async Task Port_guard_isolates_llm_gateway_to_port_8080_and_blocks_other_routes(string method, int localPort, string path)
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();

        var gateway = factory.Services.GetRequiredService<ISandboxGateway>();
        var session = gateway.Register(Guid.CreateVersion7());

        var context = await SendLlmAsync(factory, localPort, path, session.Token, method);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    // --- Proxy CONNECT (:8081) --------------------------------------------------------------

    [Fact]
    public async Task NuGet_connect_proxy_tunnels_allowed_hosts_returns_502_on_unreachable_and_blocks_disallowed_targets_emitting_EgressBlocked()
    {
        using var echoServer = new TcpListener(IPAddress.Loopback, 0);
        echoServer.Start();
        var echoPort = ((IPEndPoint)echoServer.LocalEndpoint).Port;
        var echoTask = Task.Run(
            async () =>
            {
                using var accepted = await echoServer.AcceptTcpClientAsync(Ct);
                await using var stream = accepted.GetStream();
                var buf = new byte[64];
                var n = await stream.ReadAsync(buf, Ct);
                await stream.WriteAsync(buf.AsMemory(0, n), Ct);
            },
            Ct);

        var sink = new CapturingRunEventSink();
        var registry = new SandboxGatewayRegistry();
        var runId = Guid.CreateVersion7();
        registry.Register(runId);

        var options = new StaticOptionsMonitor<SandboxOptions>(new SandboxOptions
        {
            ConnectProxyPort = 0, // Puerto efímero libre para la prueba.
            AllowedNuGetHosts = ["api.nuget.org:443", $"127.0.0.1:{echoPort}", "127.0.0.1:1"],
        });

        await using var proxy = new NuGetConnectProxy(registry, sink, options, NullLogger<NuGetConnectProxy>.Instance);
        await proxy.StartAsync(Ct);
        try
        {
            Assert.True(proxy.BoundPort > 0);

            // 1) Destino permitido: 200 Connection Established y túnel TCP bidireccional sin inspección TLS.
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
                await using var stream = client.GetStream();
                await SendRawAsync(stream, $"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n");

                var statusLine = await ReadHeadersAsync(stream);
                Assert.StartsWith("HTTP/1.1 200 Connection Established", statusLine, StringComparison.Ordinal);

                var payload = "tls-client-hello-bytes"u8.ToArray();
                await stream.WriteAsync(payload, Ct);
                var echoed = new byte[payload.Length];
                await stream.ReadExactlyAsync(echoed, Ct);
                Assert.Equal(payload, echoed);
            }

            await echoTask;
            Assert.Empty(sink.Events);

            // 2) Destino permitido pero inalcanzable: 502 Bad Gateway (no 403 ni EgressBlocked).
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
                await using var stream = client.GetStream();
                await SendRawAsync(stream, "CONNECT 127.0.0.1:1 HTTP/1.1\r\nHost: 127.0.0.1:1\r\n\r\n");

                var statusLine = await ReadHeadersAsync(stream);
                Assert.StartsWith("HTTP/1.1 502 Bad Gateway", statusLine, StringComparison.Ordinal);
            }

            Assert.Empty(sink.Events);

            // 3) Destino fuera de la allowlist: 403 Forbidden y evento EgressBlocked.
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
                await using var stream = client.GetStream();
                await SendRawAsync(stream, "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n");

                var statusLine = await ReadHeadersAsync(stream);
                Assert.StartsWith("HTTP/1.1 403 Forbidden", statusLine, StringComparison.Ordinal);
            }

            var blockedHost = await WaitForEventCountAsync(sink, 1);
            Assert.Equal(runId, blockedHost.RunId);
            var hostEvent = Assert.IsType<EgressBlocked>(blockedHost.Payload);
            Assert.Equal("example.com:443", hostEvent.Target);
            Assert.Contains("lista permitida de NuGet", hostEvent.Reason, StringComparison.OrdinalIgnoreCase);

            // 4) Método distinto de CONNECT (p. ej. GET HTTP plano): 403 Forbidden y evento EgressBlocked.
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
                await using var stream = client.GetStream();
                await SendRawAsync(stream, "GET http://api.nuget.org/v3/index.json HTTP/1.1\r\nHost: api.nuget.org\r\n\r\n");

                var statusLine = await ReadHeadersAsync(stream);
                Assert.StartsWith("HTTP/1.1 403 Forbidden", statusLine, StringComparison.Ordinal);
            }

            var methodBlocked = await WaitForEventCountAsync(sink, 2);
            var methodEvent = Assert.IsType<EgressBlocked>(methodBlocked.Payload);
            Assert.Equal("api.nuget.org", methodEvent.Target);
        }
        finally
        {
            await proxy.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task NuGet_connect_proxy_in_host_binds_AllowedNuGetHosts_and_persists_EgressBlocked_via_IRunReader()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Sandbox:ConnectProxyPort"] = "0",
            ["CODENETIX:Sandbox:AllowedNuGetHosts"] = "api.nuget.org:443, myget.corp.local:443",
        };

        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        using var http = factory.CreateClient();
        (await http.GetAsync("/api/health", Ct)).EnsureSuccessStatusCode();

        var sandboxOptions = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<SandboxOptions>>().CurrentValue;
        Assert.Equal(["api.nuget.org:443", "myget.corp.local:443"], sandboxOptions.AllowedNuGetHosts);
        Assert.True(sandboxOptions.ConnectProxyPort > 0);

        var runId = await SeedRunAsync(factory.Services);
        var gateway = factory.Services.GetRequiredService<ISandboxGateway>();
        gateway.Register(runId);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, sandboxOptions.ConnectProxyPort, Ct);
            await using var stream = client.GetStream();
            await SendRawAsync(stream, "CONNECT untrusted.example.org:443 HTTP/1.1\r\nHost: untrusted.example.org:443\r\n\r\n");

            var statusLine = await ReadHeadersAsync(stream);
            Assert.StartsWith("HTTP/1.1 403 Forbidden", statusLine, StringComparison.Ordinal);
        }

        var reader = factory.Services.GetRequiredService<IRunReader>();
        IReadOnlyList<RunEvent> events = [];
        for (var i = 0; i < 50; i++)
        {
            events = await reader.GetEventsAsync(runId, 0, Ct);
            if (events.OfType<EgressBlocked>().Any())
            {
                break;
            }

            await Task.Delay(20, Ct);
        }

        var blocked = Assert.Single(events.OfType<EgressBlocked>());
        Assert.Equal("untrusted.example.org:443", blocked.Target);
        Assert.Contains("lista permitida de NuGet", blocked.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SandboxGatewayRegistry_ResolveRunId_distinguishes_multiple_sessions_and_rejects_unauthenticated_or_unknown_ip()
    {
        var registry = new SandboxGatewayRegistry();
        var runA = Guid.CreateVersion7();
        var runB = Guid.CreateVersion7();

        var sessionA = registry.Register(runA);
        registry.UpdateContainerIp(runA, "172.30.0.2");

        // Con una sola sesión activa y ContainerIp conocido, una IP no-loopback distinta no hace fallback ciego.
        Assert.Null(registry.ResolveRunId(proxyAuthorization: null, remoteIp: "10.99.0.5"));
        Assert.Equal(runA, registry.ResolveRunId(proxyAuthorization: null, remoteIp: "127.0.0.1"));

        var sessionB = registry.Register(runB);
        registry.UpdateContainerIp(runB, "172.30.0.3");

        // Con dos sesiones activas, resuelve por ContainerIp o por Proxy-Authorization válido.
        Assert.Equal(runA, registry.ResolveRunId(proxyAuthorization: null, remoteIp: "172.30.0.2"));
        Assert.Equal(runB, registry.ResolveRunId(proxyAuthorization: null, remoteIp: "172.30.0.3"));
        Assert.Null(registry.ResolveRunId(proxyAuthorization: null, remoteIp: "172.30.0.99"));
        Assert.Null(registry.ResolveRunId(proxyAuthorization: null, remoteIp: "127.0.0.1"));

        Assert.Equal(runA, registry.ResolveRunId($"Bearer {sessionA.Token}", remoteIp: "172.30.0.99"));
        var basicB = Convert.ToBase64String(Encoding.UTF8.GetBytes($"sandbox:{sessionB.Token}"));
        Assert.Equal(runB, registry.ResolveRunId($"Basic {basicB}", remoteIp: "172.30.0.99"));

        // Un GUID o token con nonce falso en Proxy-Authorization no autentica la sesión.
        var forgedBasic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{runA:D}:wrong"));
        Assert.Null(registry.ResolveRunId($"Basic {forgedBasic}", remoteIp: "172.30.0.99"));
        Assert.Null(registry.ResolveRunId($"Bearer cnx-run-{runA:D}-00000000000000000000000000000000", remoteIp: "172.30.0.99"));

        // UpdateContainerIp después de Revoke no resucita la sesión revocada.
        Assert.True(registry.Revoke(runA));
        registry.UpdateContainerIp(runA, "172.30.0.2");
        Assert.False(registry.TryAuthenticate(sessionA.Token, out _));
    }

    // --- Validación de configuración de Coder -----------------------------------------------

    [Fact]
    public async Task Coder_provider_validation_reports_diagnostic_when_provider_or_api_key_is_missing()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Agents:Coder:Provider"] = "OpenAI",
        };

        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health", Ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Diagnostic", doc.RootElement.GetProperty("status").GetString());
        var reasons = doc.RootElement.GetProperty("reasons").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(reasons, r => r.Contains("CODENETIX__Agents__Coder__Provider", StringComparison.Ordinal)
                                      && r.Contains("CODENETIX__Providers__OpenAI__ApiKey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coder_provider_validation_reports_diagnostic_when_custom_provider_is_missing_model()
    {
        var settings = new Dictionary<string, string?>(CodeNetixFactory.CompleteSettings)
        {
            ["CODENETIX:Providers:CustomCoder:ApiKey"] = RealProviderKey,
            ["CODENETIX:Providers:CustomCoder:BaseUrl"] = "https://llm.corp.local/v1/",
            ["CODENETIX:Agents:Coder:Provider"] = "CustomCoder",
        };

        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, settings);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health", Ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Diagnostic", doc.RootElement.GetProperty("status").GetString());
        var reasons = doc.RootElement.GetProperty("reasons").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(reasons, r => r.Contains("CODENETIX__Agents__Coder__Model", StringComparison.Ordinal));
    }

    // --- Utilidades -------------------------------------------------------------------------

    private static async Task<Guid> SeedRunAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);
        var (run, _, _) = PipelineRun.Create(Guid.CreateVersion7(), "github:o/r#1", DateTimeOffset.UtcNow);
        db.Runs.Add(run);
        await db.SaveChangesAsync(Ct);
        return run.Id;
    }

    private static Task<HttpContext> SendLlmAsync(CodeNetixFactory factory, int localPort, string path, string? apiKey, string method = "POST") =>
        factory.Server.SendAsync(
            c =>
            {
                c.Connection.LocalPort = localPort;
                c.Request.Method = method;
                c.Request.Path = path;
                if (apiKey is not null)
                {
                    c.Request.Headers["x-api-key"] = apiKey;
                }
            },
            Ct);

    private static ValueTask SendRawAsync(NetworkStream stream, string text) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), Ct);

    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (builder.Length < 4096)
        {
            var read = await stream.ReadAsync(buffer, Ct);
            if (read == 0)
            {
                break;
            }

            builder.Append((char)buffer[0]);
            if (builder.Length >= 4 && builder.ToString(builder.Length - 4, 4) == "\r\n\r\n")
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static async Task<(Guid RunId, Guid? StageId, RunEvent Payload)> WaitForEventCountAsync(CapturingRunEventSink sink, int expectedCount)
    {
        for (var i = 0; i < 50; i++)
        {
            var events = sink.Events;
            if (events.Count >= expectedCount)
            {
                return events[^1];
            }

            await Task.Delay(20, Ct);
        }

        Assert.Equal(expectedCount, sink.Events.Count);
        return sink.Events[^1];
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;

        public T Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class CapturingRunEventSink : IRunEventSink
    {
        private readonly ConcurrentQueue<(Guid RunId, Guid? StageId, RunEvent Payload)> _events = new();

        public IReadOnlyList<(Guid RunId, Guid? StageId, RunEvent Payload)> Events => [.. _events];

        public Task<RunEvent> AppendAsync(Guid runId, Guid? stageId, RunEvent payload, CancellationToken cancellationToken)
        {
            var stamped = payload with { RunId = runId, Seq = _events.Count + 1, At = DateTimeOffset.UtcNow };
            _events.Enqueue((runId, stageId, stamped));
            return Task.FromResult(stamped);
        }
    }

    private sealed class FakeLlmServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<CapturedRequest> _requests = new();

        private FakeLlmServer(Func<HttpContext, Task> handler)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            _app = builder.Build();
            _app.Run(async context =>
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var rawHeaders = new StringBuilder();
                foreach (var header in context.Request.Headers)
                {
                    headers[header.Key] = header.Value.ToString();
                    rawHeaders.Append(header.Key).Append(": ").AppendLine(header.Value.ToString());
                }

                using var bodyReader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                var body = await bodyReader.ReadToEndAsync();

                _requests.Enqueue(new CapturedRequest(
                    context.Request.Method,
                    context.Request.Path.Value ?? string.Empty,
                    context.Request.QueryString.Value ?? string.Empty,
                    headers,
                    rawHeaders.ToString(),
                    body));

                await handler(context);
            });
        }

        public string BaseUrl { get; private set; } = string.Empty;

        public IReadOnlyList<CapturedRequest> Requests => [.. _requests];

        public static async Task<FakeLlmServer> StartAsync(Func<HttpContext, Task> handler)
        {
            var server = new FakeLlmServer(handler);
            await server._app.StartAsync();
            server.BaseUrl = server._app.Urls.First().TrimEnd('/');
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        public sealed record CapturedRequest(
            string Method,
            string Path,
            string Query,
            IReadOnlyDictionary<string, string> Headers,
            string RawHeaders,
            string Body);
    }
}
