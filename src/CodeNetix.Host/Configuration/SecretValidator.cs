using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodeNetix.Host.Configuration;

/// <summary>Resultado de comprobar una clave: <see cref="Valid"/> y un mensaje en español sin el secreto.</summary>
internal sealed record SecretCheck(bool Valid, string Message);

/// <summary>
/// Comprueba una clave contra el servicio real antes de guardarla desde el Panel (historia 8.1):
/// <list type="bullet">
/// <item>GitHub: <c>GET {ApiUrl}/user</c> con <c>Authorization: Bearer</c>; informa del login y, si GitHub envía
/// <c>X-OAuth-Scopes</c> (token clásico), de sus permisos.</item>
/// <item>Proveedor: <c>GET {BaseUrl}/models</c> (la llamada autenticada más barata). Anthropic con <c>x-api-key</c>,
/// <c>Authorization: Bearer</c> y <c>anthropic-version</c> (las mismas credenciales que inyecta el Egress Gateway); el
/// resto con <c>Authorization: Bearer</c>.</item>
/// </list>
/// Un único plazo (10 s) cubre la petición y la lectura del cuerpo, que además se acota a 64 KiB; sin seguir
/// redirecciones (el secreto no sale del host configurado) y sin citar el cuerpo de los errores: los mensajes nunca
/// contienen el secreto.
/// </summary>
internal sealed class SecretValidator : IDisposable
{
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>La misma versión de la API REST que usa el módulo GitHub.</summary>
    public const string GitHubApiVersion = "2022-11-28";

    /// <summary>Más que suficiente para <c>/user</c>; un cuerpo mayor se trata como respuesta no reconocible.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public SecretValidator()
        : this(DefaultTimeout)
    {
    }

    internal SecretValidator(TimeSpan timeout)
    {
        _timeout = timeout;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // El plazo lo controla cada comprobación (para distinguir "sin respuesta" de una cancelación del cliente).
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<SecretCheck> CheckGitHubTokenAsync(string token, string apiUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var rawUrl = string.IsNullOrWhiteSpace(apiUrl) ? CodeNetix.GitHub.GitHubOptions.DefaultApiUrl : apiUrl.Trim();
        if (!Uri.TryCreate(rawUrl.EndsWith('/') ? rawUrl : rawUrl + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            return new SecretCheck(false, "CODENETIX__GitHub__ApiUrl no es una URL http(s) válida: corrígela antes de guardar el token.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "user"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodeNetix", "1.0"));

        return await RunAsync("GitHub", baseUri, cancellationToken, async timeoutToken =>
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.Unauthorized:
                    return new SecretCheck(false, "GitHub rechaza el token: no es válido, está mal copiado o ha caducado.");
                case HttpStatusCode.Forbidden when IsRateLimited(response):
                case HttpStatusCode.TooManyRequests:
                    return new SecretCheck(false, "GitHub ha limitado las peticiones (rate limit); vuelve a intentarlo en unos minutos.");
                case HttpStatusCode.Forbidden:
                    return new SecretCheck(false, "GitHub rechaza el token (HTTP 403): no tiene permiso para leer tu cuenta. Revisa sus permisos.");
                default:
                    return status is >= 300 and < 400
                        ? new SecretCheck(false, $"GitHub responde con una redirección (HTTP {status}); no se sigue. Revisa CODENETIX__GitHub__ApiUrl.")
                        : new SecretCheck(false, $"GitHub no responde correctamente (HTTP {status}); vuelve a intentarlo en unos minutos.");
            }

            // La lectura del cuerpo queda dentro del mismo plazo.
            var login = await ReadLoginAsync(response, timeoutToken).ConfigureAwait(false);
            if (login is null)
            {
                return new SecretCheck(false, "GitHub no ha devuelto una respuesta reconocible; revisa CODENETIX__GitHub__ApiUrl.");
            }

            return new SecretCheck(true, DescribeGitHubToken(login, response.Headers.TryGetValues("X-OAuth-Scopes", out var scopes) ? string.Join(',', scopes) : null));
        }).ConfigureAwait(false);
    }

    public async Task<SecretCheck> CheckProviderKeyAsync(string provider, Uri baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, "models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (string.Equals(provider, AgentEndpointResolver.Anthropic, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }

        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodeNetix", "1.0"));

        return await RunAsync(provider, baseUrl, cancellationToken, async timeoutToken =>
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new SecretCheck(true, $"Key válida: {provider} la ha aceptado."),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new SecretCheck(false, $"{provider} rechaza la key: no es válida, está mal copiada o se ha revocado."),
                HttpStatusCode.NotFound =>
                    new SecretCheck(false, $"No se encuentra la API de {provider} en {baseUrl.GetLeftPart(UriPartial.Authority)} (HTTP 404); revisa CODENETIX__Providers__{provider}__BaseUrl."),
                HttpStatusCode.TooManyRequests =>
                    new SecretCheck(false, $"{provider} ha limitado las peticiones (rate limit); vuelve a intentarlo en unos minutos."),
                _ when status is >= 300 and < 400 =>
                    new SecretCheck(false, $"{provider} responde con una redirección (HTTP {status}); no se sigue. Revisa CODENETIX__Providers__{provider}__BaseUrl."),
                _ => new SecretCheck(false, $"{provider} no responde correctamente (HTTP {status}); vuelve a intentarlo en unos minutos."),
            };
        }).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Login, tipo de token y, si GitHub los informa, permisos (los fine-grained no se pueden listar aquí).</summary>
    internal static string DescribeGitHubToken(string login, string? oauthScopes)
    {
        if (oauthScopes is null)
        {
            return $"Token válido para la cuenta @{login}. GitHub no informa de los permisos de un token fine-grained: comprueba que tiene Contents, Issues y Pull requests en lectura y escritura y Metadata en lectura en los repositorios que vaya a usar CodeNetix.";
        }

        var scopes = oauthScopes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var description = $"Token válido para la cuenta @{login} (token clásico; permisos: {(scopes.Count == 0 ? "ninguno" : string.Join(", ", scopes))}).";
        if (!scopes.Contains("repo", StringComparer.Ordinal) && !scopes.Contains("public_repo", StringComparer.Ordinal))
        {
            description += " Aviso: le falta el permiso 'repo': CodeNetix no podrá clonar, comentar ni abrir PR.";
        }

        return description;
    }

    /// <summary>Ejecuta la comprobación con un único plazo (petición y cuerpo) y traduce red y timeout a mensajes.</summary>
    private async Task<SecretCheck> RunAsync(string service, Uri target, CancellationToken cancellationToken, Func<CancellationToken, Task<SecretCheck>> check)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            return await check(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SecretCheck(false, $"{service} no responde (sin respuesta en {Math.Max(1, (int)Math.Round(_timeout.TotalSeconds))} s); vuelve a intentarlo.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // La excepción original no se cita: podría arrastrar detalles de la petición.
            return new SecretCheck(false, $"No se puede conectar con {service} ({target.Host}): comprueba la conexión a internet del contenedor.");
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ratelimit-remaining", out var values) && values.FirstOrDefault() == "0";

    /// <summary><c>login</c> de <c>/user</c>, leyendo como mucho <see cref="MaxBodyBytes"/>; <c>null</c> si no se reconoce.</summary>
    private static async Task<string?> ReadLoginAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxBodyBytes)
        {
            return null;
        }

        byte[] body;
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBodyBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            body = buffer.ToArray();
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("login", out var login)
                && login.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(login.GetString())
                    ? login.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
