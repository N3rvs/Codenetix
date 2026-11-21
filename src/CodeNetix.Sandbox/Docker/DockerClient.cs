using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeNetix.Sandbox.Docker;

/// <summary>
/// Cliente mínimo de la API HTTP del motor Docker (v1.43 o superior) sobre <see cref="HttpClient"/>: sin CLI y sin
/// Docker.DotNet. Solo lo que necesita el Sandbox: <c>/_ping</c>, versión, imágenes (inspeccionar), redes (inspeccionar,
/// crear <c>internal</c> y conectar el host como <c>gateway</c>), volúmenes (crear y borrar), contenedores (crear,
/// iniciar, parar, matar, borrar, inspeccionar, <c>PUT archive</c>) y exec (crear, iniciar con el flujo multiplexado e
/// inspeccionar).
/// </summary>
internal sealed class DockerClient : IDisposable
{
    /// <summary>Versión mínima de la API que se usa (la de Docker Engine 24).</summary>
    public static readonly Version MinimumApiVersion = new(1, 43);

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _versionGate = new(1, 1);
    private string? _versionPrefix;

    public DockerClient(DockerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Endpoint = endpoint;

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            UseProxy = false,
            AllowAutoRedirect = false,
        };
        if (endpoint.Transport != DockerTransport.Tcp)
        {
            handler.ConnectCallback = (_, ct) => endpoint.ConnectAsync(ct);
        }

        // Cada llamada fija su propio plazo (el flujo de un exec puede durar lo que dure el comando).
        _http = new HttpClient(handler) { BaseAddress = endpoint.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
    }

    public DockerEndpoint Endpoint { get; }

    public void Dispose()
    {
        _http.Dispose();
        _versionGate.Dispose();
    }

    // --- Sistema ----------------------------------------------------------------------------

    /// <summary><c>GET /_ping</c>: lanza si el motor no responde <c>OK</c>.</summary>
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "/_ping", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /version</c> (una vez): usa la v1.43 salvo que el motor exija una mínima mayor. Lanza
    /// <see cref="DockerApiException"/> si el motor es anterior a la v1.43.
    /// </summary>
    public async Task<string> GetApiPrefixAsync(CancellationToken cancellationToken)
    {
        if (_versionPrefix is { } cached)
        {
            return cached;
        }

        await _versionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_versionPrefix is { } again)
            {
                return again;
            }

            using var response = await SendAsync(HttpMethod.Get, "/version", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            var api = ParseVersion(root, "ApiVersion") ?? MinimumApiVersion;
            var min = ParseVersion(root, "MinAPIVersion") ?? MinimumApiVersion;
            if (api < MinimumApiVersion)
            {
                throw new DockerApiException(0, $"el motor Docker ofrece la API {api} y CodeNetix necesita la {MinimumApiVersion} o superior");
            }

            var chosen = min > MinimumApiVersion ? min : MinimumApiVersion;
            _versionPrefix = $"/v{chosen.Major}.{chosen.Minor}";
            return _versionPrefix;
        }
        finally
        {
            _versionGate.Release();
        }
    }

    // --- Imágenes ---------------------------------------------------------------------------

    public async Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Get, $"/images/{EscapeName(image)}/json", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // --- Redes ------------------------------------------------------------------------------

    /// <summary><c>GET /networks/{name}</c>; <c>null</c> si no existe.</summary>
    public async Task<JsonDocument?> InspectNetworkAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Get, $"/networks/{EscapeName(name)}", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>POST /networks/create</c> con <c>Driver: bridge</c> e <c>Internal: true</c>.</summary>
    public async Task CreateNetworkAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["Name"] = name,
            ["Driver"] = "bridge",
            ["Internal"] = true,
            ["Labels"] = ToJsonObject(labels),
        };
        using var response = await SendVersionedAsync(HttpMethod.Post, "/networks/create", body, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>POST /networks/{name}/connect</c>: une <paramref name="containerIdOrName"/> con el alias <paramref name="alias"/>.</summary>
    public async Task ConnectNetworkAsync(string networkName, string containerIdOrName, string alias, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["Container"] = containerIdOrName,
            ["EndpointConfig"] = new JsonObject
            {
                ["Aliases"] = new JsonArray(alias),
            },
        };
        using var response = await SendVersionedAsync(HttpMethod.Post, $"/networks/{EscapeName(networkName)}/connect", body, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /networks/{name}</c>; <c>false</c> si no existía.</summary>
    public async Task<bool> RemoveNetworkAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Delete, $"/networks/{EscapeName(name)}", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // --- Volúmenes --------------------------------------------------------------------------

    /// <summary><c>POST /volumes/create</c>; si ya existe uno con ese nombre, Docker lo devuelve tal cual.</summary>
    public async Task CreateVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["Name"] = name,
            ["Driver"] = "local",
            ["Labels"] = ToJsonObject(labels),
        };
        using var response = await SendVersionedAsync(HttpMethod.Post, "/volumes/create", body, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /volumes/{name}?force=true</c>; <c>false</c> si no existía.</summary>
    public async Task<bool> RemoveVolumeAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Delete, $"/volumes/{EscapeName(name)}?force=true", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary><c>GET /volumes/{name}</c>: ¿existe el volumen?</summary>
    public async Task<bool> VolumeExistsAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Get, $"/volumes/{EscapeName(name)}", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary><c>GET /volumes?filters={"label":["k=v"]}</c>: nombres de los volúmenes con esa etiqueta.</summary>
    public async Task<IReadOnlyList<string>> ListVolumeNamesAsync(string label, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Get, $"/volumes?filters={LabelFilter(label)}", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return json.RootElement.TryGetProperty("Volumes", out var volumes) && volumes.ValueKind == JsonValueKind.Array
            ? [.. volumes.EnumerateArray().Select(v => v.GetProperty("Name").GetString()!)]
            : [];
    }

    // --- Contenedores -----------------------------------------------------------------------

    /// <summary><c>GET /containers/json?all=true&amp;filters={"label":["k=v"]}</c>: ids de los contenedores con esa etiqueta.</summary>
    public async Task<IReadOnlyList<string>> ListContainerIdsAsync(string label, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Get, $"/containers/json?all=true&filters={LabelFilter(label)}", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return [.. json.RootElement.EnumerateArray().Select(c => c.GetProperty("Id").GetString()!)];
    }

    /// <summary><c>POST /containers/create?name=…</c>; devuelve el id.</summary>
    public async Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Post, $"/containers/create?name={Uri.EscapeDataString(name)}", body, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return json.RootElement.GetProperty("Id").GetString() ?? throw new DockerApiException(0, "Docker no ha devuelto el id del contenedor");
    }

    /// <summary><c>PUT /containers/{id}/archive?path=…</c>: extrae el tar de <paramref name="content"/> en <paramref name="path"/>.</summary>
    public async Task PutArchiveAsync(string id, string path, HttpContent content, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(
            HttpMethod.Put,
            $"/containers/{EscapeName(id)}/archive?path={Uri.EscapeDataString(path)}&noOverwriteDirNonDir=true",
            content,
            timeout,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tamaño máximo permitido al descargar un tar de un contenedor (256 MiB).</summary>
    public const long MaxArchiveBytes = 256L * 1024 * 1024;

    /// <summary><c>GET /containers/{id}/archive?path=…</c>: descarga el tar de <paramref name="path"/> en <paramref name="destination"/>.</summary>
    public async Task GetArchiveAsync(string id, string path, Stream destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var prefix = await GetApiPrefixAsync(cancellationToken).ConfigureAwait(false);
        var requestPath = prefix + $"/containers/{EscapeName(id)}/archive?path={Uri.EscapeDataString(path)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, timeoutSource.Token).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long totalBytes = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, timeoutSource.Token).ConfigureAwait(false)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > MaxArchiveBytes)
                    {
                        throw new IOException($"el archivo exportado de '{path}' supera el límite máximo de {MaxArchiveBytes / (1024 * 1024)} MiB");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Docker no ha respondido a GET {requestPath} en {timeout.TotalSeconds:0} s", ex);
        }
    }

    /// <summary><c>POST /containers/{id}/wait</c>: espera a que el contenedor termine y devuelve su código de salida.</summary>
    public async Task<int> WaitContainerAsync(string id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Post, $"/containers/{EscapeName(id)}/wait", null, timeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return json.RootElement.TryGetProperty("StatusCode", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : -1;
    }

    public async Task StartContainerAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Post, $"/containers/{EscapeName(id)}/start", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>POST /containers/{id}/stop?t=…</c>: 304 (ya parado) y 404 cuentan como éxito.</summary>
    public async Task StopContainerAsync(string id, int graceSeconds, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(
            HttpMethod.Post,
            $"/containers/{EscapeName(id)}/stop?t={graceSeconds.ToString(CultureInfo.InvariantCulture)}",
            null,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>POST /containers/{id}/kill</c>: 404 y 409 (no está en marcha) cuentan como éxito.</summary>
    public async Task KillContainerAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Post, $"/containers/{EscapeName(id)}/kill", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /containers/{id}?force=true</c>; <c>false</c> si no existía.</summary>
    public async Task<bool> RemoveContainerAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Delete, $"/containers/{EscapeName(id)}?force=true", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary><c>GET /containers/{id}/json</c>; <c>null</c> si no existe.</summary>
    public async Task<JsonDocument?> InspectContainerAsync(string idOrName, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Get, $"/containers/{EscapeName(idOrName)}/json", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // --- Exec -------------------------------------------------------------------------------

    /// <summary><c>POST /containers/{id}/exec</c>; devuelve el id del exec.</summary>
    public async Task<string> CreateExecAsync(string id, JsonObject body, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Post, $"/containers/{EscapeName(id)}/exec", body, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return json.RootElement.GetProperty("Id").GetString() ?? throw new DockerApiException(0, "Docker no ha devuelto el id del exec");
    }

    /// <summary>
    /// <c>POST /exec/{id}/start</c> sin TTY: la respuesta es el flujo multiplexado (stdout/stderr) hasta que el comando
    /// termina. El llamante libera la respuesta.
    /// </summary>
    public async Task<HttpResponseMessage> StartExecAsync(string execId, CancellationToken cancellationToken)
    {
        var prefix = await GetApiPrefixAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, prefix + $"/exec/{EscapeName(execId)}/start")
        {
            Content = JsonContent(new JsonObject { ["Detach"] = false, ["Tty"] = false }),
        };

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary><c>GET /exec/{id}/json</c>: (<c>Running</c>, <c>ExitCode</c>).</summary>
    public async Task<(bool Running, int? ExitCode)> InspectExecAsync(string execId, CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Get, $"/exec/{EscapeName(execId)}/json", null, DefaultRequestTimeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var running = root.TryGetProperty("Running", out var r) && r.ValueKind == JsonValueKind.True;
        int? exitCode = root.TryGetProperty("ExitCode", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;
        return (running, exitCode);
    }

    // --- Transporte -------------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendVersionedAsync(HttpMethod method, string path, object? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var prefix = await GetApiPrefixAsync(cancellationToken).ConfigureAwait(false);
        return await SendAsync(method, prefix + path, body, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Envía la petición y lee la respuesta completa dentro de <paramref name="timeout"/>.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = body switch
            {
                null => null,
                HttpContent content => content,
                JsonNode node => JsonContent(node),
                _ => throw new ArgumentException("Cuerpo no admitido.", nameof(body)),
            },
        };

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Docker no ha respondido a {method} {path} en {timeout.TotalSeconds:0} s", ex);
        }
    }

    private static StringContent JsonContent(JsonNode node)
    {
        var content = new StringContent(node.ToJsonString(JsonOptions), new UTF8Encoding(false));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = string.Empty;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.TrimStart().StartsWith('{'))
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    message = m.GetString() ?? string.Empty;
                }
            }
            else
            {
                message = text.Trim();
            }
        }
        catch (JsonException)
        {
            // Sin cuerpo legible: solo el código.
        }

        if (message.Length == 0)
        {
            message = $"HTTP {(int)response.StatusCode}";
        }

        throw new DockerApiException((int)response.StatusCode, message.Length <= 300 ? message : string.Concat(message.AsSpan(0, 299), "…"));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static Version? ParseVersion(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && Version.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
        {
            obj[key] = value;
        }

        return obj;
    }

    private static string LabelFilter(string label) =>
        Uri.EscapeDataString(new JsonObject { ["label"] = new JsonArray(label) }.ToJsonString());

    /// <summary>Escapa un nombre o id para la ruta conservando <c>/</c> (imágenes con registro u organización).</summary>
    private static string EscapeName(string value) =>
        Uri.EscapeDataString(value).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Error de la API de Docker: <see cref="StatusCode"/> HTTP (0 si no viene de una respuesta) y su mensaje.</summary>
internal sealed class DockerApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
