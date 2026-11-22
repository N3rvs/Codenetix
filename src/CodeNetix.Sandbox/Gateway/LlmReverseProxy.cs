using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace CodeNetix.Sandbox.Gateway;

public static class LlmReverseProxyEndpointExtensions
{
    public static IEndpointRouteBuilder MapLlmGateway(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map(LlmReverseProxy.Route, (HttpContext context, string? rest, LlmReverseProxy proxy) => proxy.HandleAsync(context, rest))
            .WithName("LlmGateway");
        return endpoints;
    }
}

/// <summary>
/// Proxy inverso LLM en <c>:8080</c> sobre YARP (<see cref="IHttpForwarder"/>, AD-7):
/// <list type="bullet">
/// <item>acepta únicamente <c>/llm/{**rest}</c> en el puerto del Gateway;</item>
/// <item>valida la credencial efímera <c>cnx-run-&lt;runId&gt;-&lt;nonce&gt;</c> (<c>x-api-key</c> o <c>Authorization: Bearer</c>) en tiempo constante;</item>
/// <item>sustituye la credencial ficticia por la key real del Proveedor del Programador sin duplicar <c>/v1</c>;</item>
/// <item>desactiva el buffering de respuesta (<see cref="IHttpResponseBodyFeature.DisableBuffering"/>) y retransmite SSE al instante;</item>
/// <item>extrae <c>model</c> y <c>usage</c> (SSE o JSON) y registra <see cref="TokensConsumed"/> en <see cref="IRunEventSink"/>.</item>
/// </list>
/// </summary>
public sealed partial class LlmReverseProxy : IDisposable
{
    public const string Prefix = "/llm";
    public const string Route = Prefix + "/{**rest}";
    public const string CoderRole = "Coder";
    public const string ApiKeyHeader = "x-api-key";
    public const string BearerScheme = "Bearer";

    private static readonly ForwarderRequestConfig RequestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(10),
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    private readonly ISandboxGateway _gateway;
    private readonly IRunEventSink _eventSink;
    private readonly IBudgetLedger? _budgetLedger;
    private readonly IOptionsMonitor<SandboxOptions> _options;
    private readonly IHttpForwarder _forwarder;
    private readonly ILogger<LlmReverseProxy> _logger;
    private readonly HttpMessageInvoker _httpClient;

    public LlmReverseProxy(
        ISandboxGateway gateway,
        IRunEventSink eventSink,
        IOptionsMonitor<SandboxOptions> options,
        IHttpForwarder forwarder,
        ILogger<LlmReverseProxy> logger,
        IBudgetLedger? budgetLedger = null,
        HttpMessageHandler? transportHandler = null)
    {
        _gateway = gateway;
        _eventSink = eventSink;
        _budgetLedger = budgetLedger;
        _options = options;
        _forwarder = forwarder;
        _logger = logger;
        _httpClient = new HttpMessageInvoker(
            transportHandler ?? new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
            },
            disposeHandler: transportHandler is null);
    }

    /// <summary>Construye la URL de destino en el Proveedor sin duplicar <c>/v1</c> si la base ya termina en <c>/v1/</c>.</summary>
    public static Uri BuildTargetUri(Uri baseUrl, string rest, QueryString query)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        var basePath = baseUrl.AbsolutePath.TrimEnd('/');
        var subPath = "/" + (rest ?? string.Empty).TrimStart('/');

        if (basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            if (subPath.Equals("/v1", StringComparison.OrdinalIgnoreCase))
            {
                subPath = string.Empty;
            }
            else if (subPath.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase))
            {
                subPath = subPath[3..];
            }
        }

        var combinedPath = string.Concat(basePath, subPath);
        if (string.IsNullOrEmpty(combinedPath))
        {
            combinedPath = "/";
        }

        var builder = new UriBuilder(baseUrl)
        {
            Path = combinedPath,
            Query = query.HasValue ? query.Value.TrimStart('?') : string.Empty,
        };
        return builder.Uri;
    }

    public static bool IsValidSubPath(string? rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            return false;
        }

        var trimmed = rest.Trim('/');
        if (trimmed.Length == 0)
        {
            return false;
        }

        foreach (var segment in trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    public async Task HandleAsync(HttpContext context, string? rest)
    {
        var settings = _options.CurrentValue;
        var expectedPort = settings.LlmGatewayPort > 0 ? settings.LlmGatewayPort : SandboxOptions.DefaultLlmGatewayPort;
        if (context.Connection.LocalPort != expectedPort || !IsValidSubPath(rest))
        {
            await Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Recurso no encontrado")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (!TryAuthenticateRequest(context.Request, out var session) || session is null)
        {
            await Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Credencial del Sandbox no válida",
                    detail: "Envía la credencial efímera activa de la Ejecución en 'x-api-key' o 'Authorization: Bearer'.")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var coder = settings.Coder;
        if (coder is null || string.IsNullOrWhiteSpace(coder.ApiKey))
        {
            await Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Proveedor del Programador no disponible")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var requestModel = await TryReadRequestModelAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        var targetUri = BuildTargetUri(coder.BaseUrl, rest!, context.Request.QueryString);
        var destinationPrefix = coder.BaseUrl.GetLeftPart(UriPartial.Authority);

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var tracker = new LlmUsageTracker(requestModel);
        var redactor = context.RequestServices.GetService<ISecretRedactor>();
        var originalBody = context.Response.Body;
        await using var responseStream = new GatewayResponseStream(context, originalBody, tracker, coder.ApiKey, redactor);
        context.Response.Body = responseStream;
        try
        {
            var transformer = new LlmProxyTransformer(targetUri, coder.ApiKey);
            await _forwarder.SendAsync(context, destinationPrefix, _httpClient, RequestConfig, transformer, context.RequestAborted).ConfigureAwait(false);
            await responseStream.CompleteResponseAsync(context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        tracker.Complete();
        if (tracker.TryGetUsage(coder.Model, out var model, out var inputTokens, out var outputTokens)
            && (inputTokens > 0 || outputTokens > 0))
        {
            try
            {
                if (_budgetLedger is not null)
                {
                    await _budgetLedger.ChargeAsync(
                        new BudgetChargeRequest(session.RunId, null, CoderRole, coder.Provider, model, inputTokens, outputTokens),
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _eventSink.AppendAsync(
                        session.RunId,
                        null,
                        new TokensConsumed(CoderRole, coder.Provider, model, inputTokens, outputTokens),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTokensNotRecorded(RunShortId.Of(session.RunId), ex.Message);
            }
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private bool TryAuthenticateRequest(HttpRequest request, out SandboxGatewaySession? session)
    {
        session = null;
        var hasApiKey = request.Headers.TryGetValue(ApiKeyHeader, out var apiKeyValues) && apiKeyValues.Count > 0;
        var hasAuth = request.Headers.TryGetValue("Authorization", out var authValues) && authValues.Count > 0;
        if (!hasApiKey && !hasAuth)
        {
            _ = _gateway.TryAuthenticate(null, out _);
            return false;
        }

        SandboxGatewaySession? byApiKey = null;
        if (hasApiKey)
        {
            if (apiKeyValues.Count != 1 || !_gateway.TryAuthenticate(apiKeyValues[0], out byApiKey) || byApiKey is null)
            {
                return false;
            }
        }

        SandboxGatewaySession? byBearer = null;
        if (hasAuth)
        {
            if (authValues.Count != 1 || !TryExtractBearer(authValues[0], out var bearer) || !_gateway.TryAuthenticate(bearer, out byBearer) || byBearer is null)
            {
                return false;
            }
        }

        if (byApiKey is not null && byBearer is not null && byApiKey.RunId != byBearer.RunId)
        {
            return false;
        }

        session = byApiKey ?? byBearer;
        return session is not null;
    }

    private static bool TryExtractBearer(string? header, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var separator = header.IndexOf(' ', StringComparison.Ordinal);
        if (separator <= 0 || !header.AsSpan(0, separator).Equals(BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = header.AsSpan(separator + 1).Trim();
        if (rest.IsEmpty)
        {
            return false;
        }

        token = rest.ToString();
        return true;
    }

    private static async Task<string?> TryReadRequestModelAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is null or <= 0 or > 512 * 1024)
        {
            return null;
        }

        if (request.ContentType is { } ct && !ct.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        request.EnableBuffering();
        try
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (buffer.Length == 0)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("model", out var m)
                && m.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(m.GetString())
                    ? m.GetString()!.Trim()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido registrar TokensConsumed de la Ejecución {ShortId}: {Reason}.")]
    private partial void LogTokensNotRecorded(string shortId, string reason);

    private sealed class LlmProxyTransformer(Uri targetUri, string apiKey) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken).ConfigureAwait(false);
            proxyRequest.RequestUri = targetUri;
            proxyRequest.Headers.Host = null;
            proxyRequest.Headers.Remove(ApiKeyHeader);
            proxyRequest.Headers.Remove("Authorization");
            proxyRequest.Headers.Remove("Proxy-Authorization");
            proxyRequest.Headers.Remove("Accept-Encoding");

            proxyRequest.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
            proxyRequest.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, apiKey);
        }

        public override async ValueTask<bool> TransformResponseAsync(
            HttpContext httpContext,
            HttpResponseMessage? proxyResponse,
            CancellationToken cancellationToken)
        {
            var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken).ConfigureAwait(false);
            httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            httpContext.Response.Headers.ContentLength = null;

            return result;
        }
    }

    /// <summary>
    /// Flujo de respuesta sin buffering para respuestas 2xx (escribe y hace <c>FlushAsync</c> en cada chunk SSE al
    /// instante mientras alimenta <see cref="LlmUsageTracker"/>) y con redacción del secreto en respuestas 4xx/5xx.
    /// </summary>
    private sealed class GatewayResponseStream(
        HttpContext context,
        Stream inner,
        LlmUsageTracker tracker,
        string apiKey,
        ISecretRedactor? redactor) : Stream
    {
        private readonly byte[] _apiKeyBytes = Encoding.UTF8.GetBytes(apiKey);
        private MemoryStream? _errorBuffer;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return;
            }

            tracker.OnBytes(buffer.Span);

            if (context.Response.StatusCode >= 400)
            {
                _errorBuffer ??= new MemoryStream();
                if (_errorBuffer.Length + buffer.Length <= 1024 * 1024)
                {
                    await _errorBuffer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (_apiKeyBytes.Length > 0 && buffer.Span.IndexOf(_apiKeyBytes) >= 0)
            {
                var redacted = RedactSecret(Encoding.UTF8.GetString(buffer.Span));
                var bytes = Encoding.UTF8.GetBytes(redacted);
                await inner.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task CompleteResponseAsync(CancellationToken cancellationToken)
        {
            if (_errorBuffer is { Length: > 0 } error)
            {
                var text = Encoding.UTF8.GetString(error.GetBuffer(), 0, (int)error.Length);
                var redacted = RedactSecret(text);
                var bytes = Encoding.UTF8.GetBytes(redacted);
                await inner.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private string RedactSecret(string text)
        {
            var result = redactor is not null ? redactor.Redact(text) : text;
            if (!string.IsNullOrEmpty(apiKey) && result.Contains(apiKey, StringComparison.Ordinal))
            {
                var mask = apiKey.Length >= 12
                    ? string.Concat(apiKey.AsSpan(0, 3), "…", apiKey.AsSpan(apiKey.Length - 4))
                    : "…";
                result = result.Replace(apiKey, mask, StringComparison.Ordinal);
            }

            return result;
        }
    }
}

/// <summary>
/// Extrae al vuelo <c>model</c> y <c>usage</c> (<c>input_tokens</c>/<c>prompt_tokens</c> y
/// <c>output_tokens</c>/<c>completion_tokens</c>) de eventos SSE (<c>message_start</c>, <c>message_delta</c> o chunks
/// OpenAI) o de una respuesta JSON sin bloquear la retransmisión.
/// </summary>
internal sealed class LlmUsageTracker(string? fallbackRequestModel = null)
{
    private const int MaxJsonFallbackBytes = 512 * 1024;
    private const int MaxLineBytes = 256 * 1024;

    private readonly MemoryStream _lineBuffer = new();
    private readonly MemoryStream _jsonFallback = new();
    private bool _sawSseData;
    private bool _lineOverflowed;
    private bool _hasUsage;
    private string? _model = fallbackRequestModel;
    private int _baseInputTokens;
    private int _cacheCreationTokens;
    private int _cacheReadTokens;
    private int _outputTokens;

    public void OnBytes(ReadOnlySpan<byte> bytes)
    {
        if (!_sawSseData && _jsonFallback.Length + bytes.Length <= MaxJsonFallbackBytes)
        {
            _jsonFallback.Write(bytes);
        }

        var remaining = bytes;
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (!_lineOverflowed)
                {
                    if (_lineBuffer.Length + remaining.Length <= MaxLineBytes)
                    {
                        _lineBuffer.Write(remaining);
                    }
                    else
                    {
                        _lineBuffer.SetLength(0);
                        _lineOverflowed = true;
                    }
                }

                break;
            }

            if (!_lineOverflowed && _lineBuffer.Length + newline <= MaxLineBytes)
            {
                _lineBuffer.Write(remaining[..newline]);
                FlushLine();
            }
            else
            {
                _lineBuffer.SetLength(0);
                _lineOverflowed = false;
            }

            remaining = remaining[(newline + 1)..];
        }
    }

    public void Complete()
    {
        if (!_lineOverflowed && _lineBuffer.Length > 0)
        {
            FlushLine();
        }

        if (!_hasUsage && _jsonFallback.Length > 0)
        {
            TryInspectJsonSpan(_jsonFallback.GetBuffer().AsSpan(0, (int)_jsonFallback.Length));
        }
    }

    public bool TryGetUsage(string defaultModel, out string model, out int inputTokens, out int outputTokens)
    {
        model = !string.IsNullOrWhiteSpace(_model) ? _model! : defaultModel;
        inputTokens = _baseInputTokens + _cacheCreationTokens + _cacheReadTokens;
        outputTokens = _outputTokens;
        return _hasUsage;
    }

    private void FlushLine()
    {
        var span = _lineBuffer.GetBuffer().AsSpan(0, (int)_lineBuffer.Length);
        if (!span.IsEmpty && span[^1] == (byte)'\r')
        {
            span = span[..^1];
        }

        if (span.StartsWith("data:"u8))
        {
            _sawSseData = true;
            var payload = span[5..];
            while (!payload.IsEmpty && (payload[0] == (byte)' ' || payload[0] == (byte)'\t'))
            {
                payload = payload[1..];
            }

            TryInspectJsonSpan(payload);
        }

        _lineBuffer.SetLength(0);
    }

    private void TryInspectJsonSpan(ReadOnlySpan<byte> utf8Json)
    {
        while (!utf8Json.IsEmpty && utf8Json[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            utf8Json = utf8Json[1..];
        }

        if (utf8Json.IsEmpty || utf8Json[0] != (byte)'{')
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray());
            InspectElement(doc.RootElement);
        }
        catch (JsonException)
        {
            // Fragmento que no es JSON completo: se ignora.
        }
    }

    private void InspectElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("model", out var modelProp)
            && modelProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(modelProp.GetString()))
        {
            _model = modelProp.GetString()!.Trim();
        }

        if (root.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
        {
            InspectUsage(usageProp);
        }

        if (root.TryGetProperty("message", out var messageProp) && messageProp.ValueKind == JsonValueKind.Object)
        {
            if (messageProp.TryGetProperty("model", out var msgModel)
                && msgModel.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(msgModel.GetString()))
            {
                _model = msgModel.GetString()!.Trim();
            }

            if (messageProp.TryGetProperty("usage", out var msgUsage) && msgUsage.ValueKind == JsonValueKind.Object)
            {
                InspectUsage(msgUsage);
            }
        }
    }

    private void InspectUsage(JsonElement usage)
    {
        if (TryGetTokenCount(usage, "input_tokens", out var input) || TryGetTokenCount(usage, "prompt_tokens", out input))
        {
            _baseInputTokens = input;
            _hasUsage = true;
        }

        if (TryGetTokenCount(usage, "cache_creation_input_tokens", out var cacheCreate))
        {
            _cacheCreationTokens = cacheCreate;
            _hasUsage = true;
        }

        if (TryGetTokenCount(usage, "cache_read_input_tokens", out var cacheRead))
        {
            _cacheReadTokens = cacheRead;
            _hasUsage = true;
        }

        if (TryGetTokenCount(usage, "output_tokens", out var output) || TryGetTokenCount(usage, "completion_tokens", out output))
        {
            _outputTokens = output;
            _hasUsage = true;
        }
    }

    private static bool TryGetTokenCount(JsonElement usage, string property, out int count)
    {
        if (usage.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out count)
            && count >= 0)
        {
            return true;
        }

        count = 0;
        return false;
    }
}
