using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CodeNetix.Ingestion.Features.ReadSentryContext;

internal enum SentryApiFailure
{
    None,

    /// <summary>Sin <c>AuthToken</c> u organización.</summary>
    NotConfigured,

    /// <summary>404/410: la incidencia no existe o el token no puede verla.</summary>
    NotFound,

    /// <summary>401/403: Sentry rechaza el token.</summary>
    Unauthorized,

    /// <summary>429.</summary>
    RateLimited,

    /// <summary>Red, timeout, 5xx, redirección, JSON ilegible o respuesta demasiado grande.</summary>
    Unavailable,
}

/// <summary>Respuesta de la API de Sentry: <see cref="Body"/> solo sin fallo; <see cref="Reason"/> nunca cita el token.</summary>
internal sealed record SentryApiResponse(JsonElement? Body, SentryApiFailure Failure, string? Reason)
{
    public bool Ok => Failure == SentryApiFailure.None;
}

/// <summary>
/// Cliente mínimo de la API de Sentry (historia 7.4). Solo llama a <see cref="SentryApiOptions.BaseUrl"/>, con
/// <c>Authorization: Bearer</c>, timeout por petición, sin seguir redirecciones (el token nunca sale de <c>BaseUrl</c>), sin
/// reintentos (un 429 se devuelve tal cual) y con un tope de tamaño de respuesta. Nunca lanza por errores de Sentry ni
/// adjunta la excepción original (podría arrastrar detalles de la petición).
/// </summary>
internal sealed class SentryApiClient : IDisposable
{
    public const string NotRespondingReason = "Sentry no responde";

    public const string RateLimitedReason = "Sentry ha limitado las peticiones (rate limit)";

    private readonly SentryApiOptions _options;
    private readonly HttpClient _http;

    public SentryApiClient(IOptions<SentryApiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
        })
        {
            // El timeout por petición lo aplica SendAsync con un token propio (así se distingue de la cancelación).
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public static string UnauthorizedReason(int status) =>
        string.Create(CultureInfo.InvariantCulture, $"Sentry rechaza el token (HTTP {status})");

    public static string NotFoundReason(string issueId) =>
        $"la incidencia de Sentry {issueId} no existe o el token no puede verla";

    /// <summary><c>GET /api/0/organizations/{org}/issues/{id}/</c>: título, proyecto y frecuencia.</summary>
    public Task<SentryApiResponse> GetIssueAsync(string issueId, CancellationToken cancellationToken) =>
        GetAsync(issueId, $"issues/{issueId}/", cancellationToken);

    /// <summary><c>GET /api/0/organizations/{org}/issues/{id}/events/latest/</c>: el último evento con su traza.</summary>
    public Task<SentryApiResponse> GetLatestEventAsync(string issueId, CancellationToken cancellationToken) =>
        GetAsync(issueId, $"issues/{issueId}/events/latest/", cancellationToken);

    public void Dispose() => _http.Dispose();

    private async Task<SentryApiResponse> GetAsync(string issueId, string relative, CancellationToken cancellationToken)
    {
        if (!_options.Configured)
        {
            return new SentryApiResponse(null, SentryApiFailure.NotConfigured, SentryApiOptions.NotConfiguredReason);
        }

        if (!Uri.TryCreate(_options.NormalizedBaseUrl + "/", UriKind.Absolute, out var baseUri))
        {
            return new SentryApiResponse(null, SentryApiFailure.Unavailable, "CODENETIX__Sentry__BaseUrl no es una URL válida");
        }

        var uri = new Uri(baseUri, $"api/0/organizations/{Uri.EscapeDataString(_options.Organization!)}/{relative}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AuthToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodeNetix", "1.0"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new SentryApiResponse(null, SentryApiFailure.Unauthorized, UnauthorizedReason(status));
                case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                    return new SentryApiResponse(null, SentryApiFailure.NotFound, NotFoundReason(issueId));
                case HttpStatusCode.TooManyRequests:
                    return new SentryApiResponse(null, SentryApiFailure.RateLimited, RateLimitedReason);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new SentryApiResponse(null, SentryApiFailure.Unavailable, string.Create(CultureInfo.InvariantCulture, $"{NotRespondingReason} (HTTP {status})"));
            }

            if (response.Content.Headers.ContentLength is { } length && length > _options.MaxResponseBytes)
            {
                return new SentryApiResponse(null, SentryApiFailure.Unavailable, "la respuesta de Sentry es demasiado grande");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > _options.MaxResponseBytes)
                {
                    return new SentryApiResponse(null, SentryApiFailure.Unavailable, "la respuesta de Sentry es demasiado grande");
                }

                buffer.Write(chunk, 0, read);
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new SentryApiResponse(null, SentryApiFailure.Unavailable, NotRespondingReason + " (respuesta inesperada)");
            }

            return new SentryApiResponse(document.RootElement.Clone(), SentryApiFailure.None, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SentryApiResponse(
                null,
                SentryApiFailure.Unavailable,
                string.Create(CultureInfo.InvariantCulture, $"{NotRespondingReason} (sin respuesta en {_options.RequestTimeout.TotalSeconds:0} s)"));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return new SentryApiResponse(null, SentryApiFailure.Unavailable, NotRespondingReason);
        }
    }
}
