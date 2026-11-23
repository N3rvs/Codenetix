using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeNetix.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeNetix.GitHub;

/// <summary>Respuesta de <see cref="GitHubApiClient.GetAsync"/>: <see cref="Body"/> solo en 2xx.</summary>
/// <param name="RateLimited">429, o 403 con <c>x-ratelimit-remaining: 0</c>.</param>
/// <param name="Link">Cabecera <c>Link</c> de paginación (solo en 2xx), o <c>null</c>.</param>
/// <param name="ErrorBody">Cuerpo JSON de error en respuestas no 2xx (cuando el servidor devuelve JSON), o <c>null</c>.</param>
/// <param name="ETag">Cabecera <c>ETag</c> (en 2xx y en <c>304</c>), o <c>null</c>.</param>
/// <param name="RetryAfter">Con <paramref name="RateLimited"/>: espera que indica GitHub (<c>retry-after</c> o <c>x-ratelimit-reset</c>), o <c>null</c>.</param>
internal sealed record GitHubResponse(
    HttpStatusCode Status,
    JsonElement? Body,
    bool RateLimited,
    string? Link = null,
    JsonElement? ErrorBody = null,
    string? ETag = null,
    TimeSpan? RetryAfter = null);

/// <summary>
/// Cliente HTTP mínimo de la API REST de GitHub. Envía <c>Authorization: Bearer</c>,
/// <c>Accept: application/vnd.github+json</c> y <c>X-GitHub-Api-Version: 2022-11-28</c>, con un timeout
/// de 10 s. Las redirecciones no se siguen automáticamente (.NET quitaría <c>Authorization</c>): se sigue
/// una sola 301/302/307/308 si va al mismo esquema y host que <c>ApiUrl</c>; si no, GitHub "no responde".
/// Los errores de red y los timeouts se convierten en <see cref="GitHubUnavailableException"/> sin citar
/// el token (la excepción original no se adjunta para no arrastrar detalles de la petición).
/// </summary>
internal sealed class GitHubApiClient : IDisposable
{
    public const string ApiVersion = "2022-11-28";
    public const string InvalidTokenReason = "el token de GitHub no es válido o no tiene permisos";
    public const string NotRespondingReason = "GitHub no responde";
    public const string RateLimitedReason = "GitHub ha limitado las peticiones (rate limit); vuelve a intentarlo más tarde";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;

    // Se lee en cada petición (no se copia el valor): en serve el token se puede cambiar desde el Panel (historia 8.1).
    private readonly IOptions<GitHubOptions> _options;
    private readonly GitSafetyGuard _safetyGuard;

    public GitHubApiClient(IOptions<GitHubOptions> options)
        : this(options, new GitSafetyGuard(NullLogger<GitSafetyGuard>.Instance), DefaultTimeout)
    {
    }

    [ActivatorUtilitiesConstructor]
    public GitHubApiClient(IOptions<GitHubOptions> options, GitSafetyGuard safetyGuard)
        : this(options, safetyGuard, DefaultTimeout)
    {
    }

    internal GitHubApiClient(IOptions<GitHubOptions> options, TimeSpan timeout)
        : this(options, new GitSafetyGuard(NullLogger<GitSafetyGuard>.Instance), timeout)
    {
    }

    internal GitHubApiClient(IOptions<GitHubOptions> options, GitSafetyGuard safetyGuard, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(safetyGuard);
        _options = options;
        _safetyGuard = safetyGuard;
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
        })
        {
            Timeout = timeout,
        };
    }

    /// <summary>
    /// <c>GET {ApiUrl}/{relativePath}</c>. Lanza <see cref="GitHubUnavailableException"/> ante red caída,
    /// timeout, cuerpo no JSON, redirección a otro host o falta de token.
    /// </summary>
    public Task<GitHubResponse> GetAsync(string relativePath, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, relativePath, null, null, cancellationToken);

    /// <summary>
    /// <c>GET</c> condicional: con <paramref name="ifNoneMatch"/> envía <c>If-None-Match</c> y un <c>304</c> vuelve con
    /// <see cref="GitHubResponse.Status"/> <c>NotModified</c>, sin cuerpo (GitHub no lo cuenta en el rate limit).
    /// </summary>
    public Task<GitHubResponse> GetAsync(string relativePath, string? ifNoneMatch, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, relativePath, null, string.IsNullOrWhiteSpace(ifNoneMatch) ? null : ifNoneMatch.Trim(), cancellationToken);

    /// <summary>
    /// <c>POST {ApiUrl}/{relativePath}</c> con <paramref name="payload"/> como JSON. Mismas reglas que
    /// <see cref="GetAsync"/> (la redirección se sigue con el mismo método y cuerpo). Sin reintentos: un
    /// <c>POST</c> no es idempotente.
    /// </summary>
    public Task<GitHubResponse> PostAsync(string relativePath, object payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SendAsync(HttpMethod.Post, relativePath, JsonSerializer.SerializeToUtf8Bytes(payload), null, cancellationToken);
    }

    private async Task<GitHubResponse> SendAsync(HttpMethod method, string relativePath, byte[]? jsonBody, string? ifNoneMatch, CancellationToken cancellationToken)
    {
        if (IsMergeEndpointPath(relativePath))
        {
            _safetyGuard.EnsureMergeForbidden("github api merge", relativePath);
        }

        var settings = _options.Value;
        var token = settings.Token?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new GitHubUnavailableException(InvalidTokenReason);
        }

        var apiUrl = string.IsNullOrWhiteSpace(settings.ApiUrl) ? GitHubOptions.DefaultApiUrl : settings.ApiUrl.Trim();
        if (!Uri.TryCreate(apiUrl.EndsWith('/') ? apiUrl : apiUrl + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new GitHubUnavailableException("CODENETIX__GitHub__ApiUrl no es una URL http(s) válida");
        }

        try
        {
            var uri = new Uri(baseUri, relativePath);
            var response = await SendOnceAsync(method, uri, jsonBody, ifNoneMatch, token, cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsRedirect(response.StatusCode))
                {
                    // Repositorio renombrado o transferido: se sigue una vez, solo dentro del mismo esquema y host.
                    var location = response.Headers.Location;
                    var target = location is null ? null : location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (target is null
                        || !string.Equals(target.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(target.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new GitHubUnavailableException(NotRespondingReason);
                    }

                    response.Dispose();
                    response = await SendOnceAsync(method, target, jsonBody, ifNoneMatch, token, cancellationToken).ConfigureAwait(false);
                }

                return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                response.Dispose();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new GitHubUnavailableException(NotRespondingReason);
        }
    }

    /// <summary>
    /// Motivo para una respuesta no satisfactoria: rate limit → reintentar más tarde; 401/403 → token;
    /// el resto (5xx, redirecciones no resueltas, otros inesperados) → GitHub no responde.
    /// </summary>
    public static string FailureReason(GitHubResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.RateLimited)
        {
            return RateLimitedReason;
        }

        return response.Status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => InvalidTokenReason,
            _ => NotRespondingReason,
        };
    }

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, Uri uri, byte[]? jsonBody, string? ifNoneMatch, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (jsonBody is not null)
        {
            request.Content = new ByteArrayContent(jsonBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodeNetix", "1.0")); // GitHub rechaza peticiones sin User-Agent.
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GitHubResponse> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var etag = response.Headers.ETag?.ToString();
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new GitHubResponse(response.StatusCode, null, false, ETag: etag);
        }

        if (!response.IsSuccessStatusCode)
        {
            JsonElement? errorBody = null;
            try
            {
                await using var errStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var errDoc = await JsonDocument.ParseAsync(errStream, cancellationToken: cancellationToken).ConfigureAwait(false);
                errorBody = errDoc.RootElement.Clone();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Respuesta de error no JSON o vacía: se ignora.
            }

            var secondaryByMessage = !IsRateLimited(response) && IsSecondaryRateLimitMessage(response.StatusCode, errorBody);
            var rateLimited = secondaryByMessage || IsRateLimited(response);
            TimeSpan? retryAfter = rateLimited ? RetryAfterOf(response) : null;
            if (secondaryByMessage && (retryAfter is null || retryAfter < SecondaryRateLimitMinimumWait))
            {
                // Límite secundario sin cabeceras de espera: GitHub pide esperar al menos un minuto.
                retryAfter = SecondaryRateLimitMinimumWait;
            }

            return new GitHubResponse(
                response.StatusCode,
                null,
                rateLimited,
                Link: null,
                ErrorBody: errorBody,
                RetryAfter: retryAfter);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var link = response.Headers.TryGetValues("Link", out var links) ? string.Join(", ", links) : null;
        return new GitHubResponse(response.StatusCode, document.RootElement.Clone(), false, link, ETag: etag);
    }

    private static bool IsMergeEndpointPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var path = relativePath.Trim();
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        path = path.TrimEnd('/');
        return path.EndsWith("/merge", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/merges", StringComparison.OrdinalIgnoreCase)
            || path.Equals("merge", StringComparison.OrdinalIgnoreCase)
            || path.Equals("merges", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    /// <summary>429; 403 con <c>x-ratelimit-remaining: 0</c> (límite primario) o con <c>retry-after</c> (límite secundario).</summary>
    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden
            && ((response.Headers.TryGetValues("x-ratelimit-remaining", out var values) && values.Any(v => v.Trim() == "0"))
                || response.Headers.RetryAfter is not null));

    /// <summary>Espera mínima ante un límite secundario reconocido solo por el mensaje del cuerpo.</summary>
    public static readonly TimeSpan SecondaryRateLimitMinimumWait = TimeSpan.FromSeconds(60);

    /// <summary>403 cuyo <c>message</c> menciona <c>secondary rate limit</c> (GitHub no siempre manda <c>retry-after</c>).</summary>
    private static bool IsSecondaryRateLimitMessage(HttpStatusCode status, JsonElement? errorBody) =>
        status == HttpStatusCode.Forbidden
        && errorBody is { ValueKind: JsonValueKind.Object } body
        && body.TryGetProperty("message", out var message)
        && message.ValueKind == JsonValueKind.String
        && message.GetString()!.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>retry-after</c> (segundos o fecha) o, si falta, <c>x-ratelimit-reset</c> (segundos Unix); nunca negativo.</summary>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var now = DateTimeOffset.UtcNow;
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            }

            if (retryAfter.Date is { } date)
            {
                return date > now ? date - now : TimeSpan.Zero;
            }
        }

        if (response.Headers.TryGetValues("x-ratelimit-reset", out var resets)
            && long.TryParse(resets.FirstOrDefault()?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var epoch)
            && epoch is > 0 and < 253402300799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
            return reset > now ? reset - now : TimeSpan.Zero;
        }

        return null;
    }
}
