using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeNetix.Host.Access;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.SubmitManualEvent;
using CodeNetix.Persistence;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host;

/// <summary>
/// <c>codenetix fix &lt;url&gt;</c> (AD-12): cliente ligero que se ejecuta dentro del contenedor
/// (<c>docker exec codenetix codenetix fix &lt;url&gt;</c>) y llama a <c>POST /api/events/manual</c>.
/// La API es <c>CODENETIX__Host__Url</c> o <see cref="DefaultApiUrl"/>; el token, <c>CODENETIX__Host__AccessToken</c>
/// o <c>&lt;DataDirectory&gt;/admin.token</c> (solo lectura). Genera un único <c>requestId</c> y reintenta hasta
/// <see cref="MaxRetries"/> veces con el mismo id ante errores de red o 5xx distintos de 501 y 503 (incluido
/// 502: GitHub no responde). Un Evento <c>Ignored</c> (actor sin permiso) sale con código 1. Con <c>--follow</c> (antes
/// o después de la URL) sigue la Ejecución hasta un Estado terminal (ver <see cref="FollowAsync"/>).
/// No construye el host web.
/// </summary>
internal static class FixMode
{
    public const string DefaultApiUrl = "http://127.0.0.1:5000";
    public const int MaxRetries = 3;

    public const string FixUsage = "Uso: codenetix fix <url-de-issue> [--follow]   (p. ej. codenetix fix https://github.com/<owner>/<repo>/issues/<número> --follow)";

    public const string FollowOption = "--follow";

    /// <summary>Intervalo de sondeo de <c>--follow</c>.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Fallos transitorios seguidos (red o 5xx) que <c>--follow</c> tolera antes de rendirse.</summary>
    public const int MaxConsecutivePollFailures = 20;

    /// <summary>
    /// Tiempo máximo sin progreso de <c>--follow</c>: sin <c>runId</c> (fase 1) o sin eventos nuevos (fase 2).
    /// </summary>
    public static readonly TimeSpan DefaultNoProgressTimeout = TimeSpan.FromMinutes(10);

    private const string AcceptedStatus = "Accepted";
    private const string IgnoredStatus = "Ignored";

    // Por encima del timeout de 10 s de las llamadas a GitHub que hace la API antes de responder.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
        IConfiguration configuration;
        try
        {
            configuration = BuildConfiguration();
        }
        catch (InstanceConfigurationException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return CodeNetixCommandLine.ExitFailure;
        }

        return await RunAsync(args, configuration, Console.Out, Console.Error);
    }

    /// <summary>
    /// La misma configuración de instancia que ve <c>serve</c>: variables <c>CODENETIX__*</c> más el archivo
    /// YAML de instancia (el entorno tiene prioridad). Lanza <see cref="InstanceConfigurationException"/>
    /// si el archivo no es válido.
    /// </summary>
    internal static IConfiguration BuildConfiguration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddEnvironmentVariables();
        configuration.AddInstanceConfigFile();
        return configuration;
    }

    internal static async Task<int> RunAsync(
        string[] args,
        IConfiguration configuration,
        TextWriter stdout,
        TextWriter stderr,
        HttpMessageHandler? handler = null,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        CancellationToken cancellationToken = default,
        TimeSpan? pollInterval = null,
        TimeSpan? noProgressTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);

        // --follow puede ir antes o después de la URL; cualquier otra opción es un error de uso.
        var follow = args.Contains(FollowOption, StringComparer.Ordinal);
        var positional = args.Where(a => !string.Equals(a, FollowOption, StringComparison.Ordinal)).ToArray();
        if (positional.Length != 1
            || string.IsNullOrWhiteSpace(positional[0])
            || positional[0].TrimStart().StartsWith("--", StringComparison.Ordinal))
        {
            await stderr.WriteLineAsync(FixUsage);
            await stderr.WriteLineAsync();
            await stderr.WriteLineAsync(CodeNetixCommandLine.Usage);
            return CodeNetixCommandLine.ExitUsage;
        }

        var url = positional[0].Trim();

        var configuredApiUrl = configuration[HostInstanceOptions.SectionName + ":Url"];
        var apiUrl = string.IsNullOrWhiteSpace(configuredApiUrl) ? DefaultApiUrl : configuredApiUrl.Trim();
        if (!Uri.TryCreate(apiUrl.EndsWith('/') ? apiUrl : apiUrl + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            await stderr.WriteLineAsync("CODENETIX__Host__Url no es una URL http(s) válida.");
            return CodeNetixCommandLine.ExitFailure;
        }

        var tokenStore = CreateTokenStore(configuration);
        string? token;
        try
        {
            token = tokenStore.ReadEffectiveTokenReadOnly();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"No se puede leer el admin-token en '{tokenStore.FilePath}': {ex.Message}");
            return CodeNetixCommandLine.ExitFailure;
        }

        if (token is null)
        {
            await stderr.WriteLineAsync(
                $"No se encuentra el admin-token: no hay CODENETIX__Host__AccessToken ni '{tokenStore.FilePath}'. "
                + "Ejecuta el comando dentro del contenedor de CodeNetix (docker exec codenetix codenetix fix <url>) cuando ya esté arrancado.");
            return CodeNetixCommandLine.ExitFailure;
        }

        var endpoint = new Uri(baseUri, SubmitManualEventEndpoint.Route.TrimStart('/'));
        var body = new FixRequest(url, Guid.CreateVersion7().ToString("D"));
        var delays = retryDelays ?? DefaultRetryDelays;

        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = RequestTimeout;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AdminTokenMiddleware.BearerScheme, token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        for (var attempt = 0; ; attempt++)
        {
            var canRetry = attempt < MaxRetries;
            try
            {
                using var response = await client.PostAsJsonAsync(endpoint, body, Json, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var created = await ReadJsonAsync<FixResponse>(response, cancellationToken);
                    if (created is null
                        || created.EventId == Guid.Empty
                        || created.Status is not (AcceptedStatus or IgnoredStatus))
                    {
                        await stderr.WriteLineAsync($"Respuesta inesperada de CodeNetix (HTTP {(int)response.StatusCode}).");
                        return CodeNetixCommandLine.ExitFailure;
                    }

                    if (string.Equals(created.Status, IgnoredStatus, StringComparison.Ordinal))
                    {
                        await stderr.WriteLineAsync(DescribeIgnored(created));
                        return CodeNetixCommandLine.ExitFailure;
                    }

                    await stdout.WriteLineAsync($"Evento {created.EventId} registrado.");
                    return follow
                        ? await FollowAsync(client, baseUri, created.EventId, stdout, stderr, pollInterval ?? DefaultPollInterval, noProgressTimeout ?? DefaultNoProgressTimeout, cancellationToken)
                        : CodeNetixCommandLine.ExitOk;
                }

                var problem = await ReadJsonAsync<FixProblem>(response, cancellationToken);
                if (canRetry && IsRetryable(response.StatusCode, problem))
                {
                    await Task.Delay(DelayFor(delays, attempt), cancellationToken);
                    continue;
                }

                await stderr.WriteLineAsync(DescribeError(response.StatusCode, problem));
                return CodeNetixCommandLine.ExitFailure;
            }
            catch (Exception ex) when (IsNetworkError(ex, cancellationToken))
            {
                if (canRetry)
                {
                    await Task.Delay(DelayFor(delays, attempt), cancellationToken);
                    continue;
                }

                await stderr.WriteLineAsync($"No se puede contactar con CodeNetix en {baseUri}: {ex.Message}");
                return CodeNetixCommandLine.ExitFailure;
            }
        }
    }

    /// <summary>
    /// <c>--follow</c>: sondea <c>GET /api/events/{id}</c> hasta tener <c>runId</c> (o <c>Ignored</c>) y después
    /// <c>GET /api/runs/{runId}/events?afterSeq=N</c>, imprimiendo una línea por evento. Termina en un Estado
    /// terminal: <c>PullRequestCreated</c> → 0; <c>Failed</c> o <c>Cancelled</c> → 1. Si una fase pasa
    /// <paramref name="noProgressTimeout"/> sin progreso, indica cómo consultarla y sale con 1.
    /// </summary>
    private static async Task<int> FollowAsync(
        HttpClient client,
        Uri baseUri,
        Guid eventId,
        TextWriter stdout,
        TextWriter stderr,
        TimeSpan pollInterval,
        TimeSpan noProgressTimeout,
        CancellationToken cancellationToken)
    {
        var failures = 0;
        var sinceProgress = System.Diagnostics.Stopwatch.StartNew();

        // 1) Esperar a que el Dispatcher cree la Ejecución (o ignore el Evento).
        Guid runId;
        var eventUri = new Uri(baseUri, $"api/events/{eventId:D}");
        while (true)
        {
            if (sinceProgress.Elapsed >= noProgressTimeout)
            {
                await stderr.WriteLineAsync(
                    $"El Evento {eventId} sigue sin Ejecución tras {Describe(noProgressTimeout)}; consulta su estado con GET /api/events/{eventId}.");
                return CodeNetixCommandLine.ExitFailure;
            }

            var poll = await PollAsync<FollowEvent>(client, eventUri, cancellationToken);
            if (poll.Transient)
            {
                if (++failures > MaxConsecutivePollFailures)
                {
                    await stderr.WriteLineAsync(poll.Error);
                    return CodeNetixCommandLine.ExitFailure;
                }

                await Task.Delay(pollInterval, cancellationToken);
                continue;
            }

            if (poll.Error is not null)
            {
                await stderr.WriteLineAsync(poll.Error);
                return CodeNetixCommandLine.ExitFailure;
            }

            failures = 0;
            var info = poll.Value!;
            if (string.Equals(info.Status, IgnoredStatus, StringComparison.Ordinal))
            {
                var reason = string.IsNullOrWhiteSpace(info.Reason) ? "sin motivo" : info.Reason;
                await stderr.WriteLineAsync($"Evento {eventId} ignorado: {reason}.");
                return CodeNetixCommandLine.ExitFailure;
            }

            if (info.RunId is { } id && id != Guid.Empty)
            {
                runId = id;
                break;
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        await stdout.WriteLineAsync($"Ejecución {runId}:");
        sinceProgress.Restart();

        // 2) Seguir sus RunEvent por orden de seq hasta un Estado terminal.
        long afterSeq = 0;
        while (true)
        {
            if (sinceProgress.Elapsed >= noProgressTimeout)
            {
                await stderr.WriteLineAsync(
                    $"La Ejecución {runId} no ha producido eventos nuevos en {Describe(noProgressTimeout)}; consulta su estado con GET /api/runs/{runId}.");
                return CodeNetixCommandLine.ExitFailure;
            }

            var eventsUri = new Uri(baseUri, string.Create(CultureInfo.InvariantCulture, $"api/runs/{runId:D}/events?afterSeq={afterSeq}"));
            var poll = await PollAsync<JsonElement>(client, eventsUri, cancellationToken);
            if (poll.Transient)
            {
                if (++failures > MaxConsecutivePollFailures)
                {
                    await stderr.WriteLineAsync(poll.Error);
                    return CodeNetixCommandLine.ExitFailure;
                }

                await Task.Delay(pollInterval, cancellationToken);
                continue;
            }

            if (poll.Error is not null || poll.Value.ValueKind != JsonValueKind.Array)
            {
                await stderr.WriteLineAsync(poll.Error ?? "Respuesta inesperada de CodeNetix al leer los eventos de la Ejecución.");
                return CodeNetixCommandLine.ExitFailure;
            }

            failures = 0;
            foreach (var item in poll.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("seq", out var seqElement) || !seqElement.TryGetInt64(out var seq) || seq <= afterSeq)
                {
                    continue;
                }

                afterSeq = seq;
                sinceProgress.Restart();
                await stdout.WriteLineAsync(FormatEvent(item, seq));

                var terminal = TerminalStateOf(item);
                if (terminal is not null)
                {
                    return string.Equals(terminal, "PullRequestCreated", StringComparison.Ordinal)
                        ? CodeNetixCommandLine.ExitOk
                        : CodeNetixCommandLine.ExitFailure;
                }
            }

            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    private static string Describe(TimeSpan timeout) =>
        timeout.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{timeout.TotalMinutes:0.#} min")
            : string.Create(CultureInfo.InvariantCulture, $"{timeout.TotalSeconds:0.###} s");

    /// <summary><c>[3] StateChanged Received → Failed (StageError: Análisis no disponible todavía)</c>.</summary>
    internal static string FormatEvent(JsonElement item, long seq)
    {
        var type = GetString(item, "type") ?? "RunEvent";
        switch (type)
        {
            case "StateChanged":
            {
                var from = GetString(item, "from");
                var to = GetString(item, "to") ?? "?";
                var line = from is null ? $"[{seq}] StateChanged → {to}" : $"[{seq}] StateChanged {from} → {to}";
                var reason = GetString(item, "failureReason");
                var detail = GetString(item, "detail");
                if (reason is not null)
                {
                    line += string.IsNullOrWhiteSpace(detail) ? $" ({reason})" : $" ({reason}: {detail})";
                }
                else if (!string.IsNullOrWhiteSpace(detail))
                {
                    line += $" ({detail})";
                }

                return line;
            }

            case "RunCreated":
                return $"[{seq}] RunCreated {GetString(item, "objectKey")}".TrimEnd();

            case "RepositoryPrepared":
                return $"[{seq}] RepositoryPrepared {GetString(item, "baseSha")}".TrimEnd();

            case "RepoConfigResolved":
                return $"[{seq}] RepoConfigResolved {GetString(item, "source")}".TrimEnd();

            case "AnalystOutputRejected":
                return $"[{seq}] AnalystOutputRejected intento {GetNumber(item, "attempt")}: {GetString(item, "error")}".TrimEnd();

            case "SandboxCreated":
                return $"[{seq}] SandboxCreated {GetString(item, "image")}".TrimEnd();

            case "SpecGenerated":
                return $"[{seq}] SpecGenerated {GetString(item, "model")} ({GetNumber(item, "attempts")} intento(s))";

            case "VerdictRecorded":
                return $"[{seq}] VerdictRecorded ronda {GetNumber(item, "round")} {GetString(item, "reviewer")} → {GetString(item, "decision")} ({GetNumber(item, "findingCount")} hallazgo(s))";

            case "ReviewerOutputRejected":
                return $"[{seq}] ReviewerOutputRejected ronda {GetNumber(item, "round")} {GetString(item, "reviewer")} intento {GetNumber(item, "attempt")}: {GetString(item, "error")}".TrimEnd();

            default:
                return $"[{seq}] {type}";
        }
    }

    private static string? TerminalStateOf(JsonElement item) =>
        string.Equals(GetString(item, "type"), "StateChanged", StringComparison.Ordinal)
        && GetString(item, "to") is { } to
        && to is "PullRequestCreated" or "Failed" or "Cancelled"
            ? to
            : null;

    private static string GetNumber(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "?";

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Un GET de sondeo: <c>Transient</c> ante errores de red o 5xx (se reintenta); <c>Error</c> sin
    /// <c>Transient</c> ante cualquier otro error (se abandona).
    /// </summary>
    private static async Task<PollResult<T>> PollAsync<T>(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(uri, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
                    return value is null
                        ? new PollResult<T>(default, false, "Respuesta inesperada de CodeNetix.")
                        : new PollResult<T>(value, false, null);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    return new PollResult<T>(default, false, "Respuesta inesperada de CodeNetix.");
                }
            }

            var problem = await ReadJsonAsync<FixProblem>(response, cancellationToken);
            return new PollResult<T>(default, (int)response.StatusCode >= 500, DescribeError(response.StatusCode, problem));
        }
        catch (Exception ex) when (IsNetworkError(ex, cancellationToken))
        {
            return new PollResult<T>(default, true, $"No se puede contactar con CodeNetix: {ex.Message}");
        }
    }

    private sealed record PollResult<T>(T? Value, bool Transient, string? Error);

    private sealed record FollowEvent(Guid EventId, string? Status, string? Reason, Guid? RunId);

    private static AdminTokenStore CreateTokenStore(IConfiguration configuration)
    {
        var instance = new InstanceOptions();
        instance.Host.AccessToken = configuration[HostInstanceOptions.SectionName + ":AccessToken"];

        var persistence = new PersistenceOptions();
        var dataDirectory = configuration[InstanceOptions.SectionName + ":Persistence:DataDirectory"];
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            persistence.DataDirectory = dataDirectory;
        }

        return new AdminTokenStore(Options.Create(instance), Options.Create(persistence));
    }

    /// <summary>
    /// 5xx transitorios. 501 (Sentry) es definitivo; 503 solo se reintenta si la instancia está
    /// <c>Starting</c> (en <c>Diagnostic</c> no cambiará sin reiniciar).
    /// </summary>
    private static bool IsRetryable(HttpStatusCode status, FixProblem? problem) => status switch
    {
        HttpStatusCode.NotImplemented => false,
        HttpStatusCode.ServiceUnavailable => string.Equals(problem?.InstanceStatus, "Starting", StringComparison.Ordinal),
        _ => (int)status >= 500,
    };

    private static bool IsNetworkError(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException
        || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested); // timeout de HttpClient

    private static TimeSpan DelayFor(IReadOnlyList<TimeSpan> delays, int attempt) =>
        delays.Count == 0 ? TimeSpan.Zero : delays[Math.Min(attempt, delays.Count - 1)];

    private static string DescribeError(HttpStatusCode status, FixProblem? problem)
    {
        if (!string.IsNullOrWhiteSpace(problem?.Detail))
        {
            return problem.Detail;
        }

        if (!string.IsNullOrWhiteSpace(problem?.Title))
        {
            return problem.Title;
        }

        return $"CodeNetix respondió HTTP {(int)status}.";
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record FixRequest(string Url, string RequestId);

    /// <summary>"Evento &lt;id&gt; ignorado: no autorizado (&lt;login&gt; no tiene permiso de escritura en &lt;owner&gt;/&lt;repo&gt;)."</summary>
    private static string DescribeIgnored(FixResponse response)
    {
        var reason = string.IsNullOrWhiteSpace(response.Reason) ? "no autorizado" : response.Reason;
        var actor = string.IsNullOrWhiteSpace(response.Actor) ? "el token de GitHub de la instancia" : response.Actor;
        return string.IsNullOrWhiteSpace(response.Repository)
            ? $"Evento {response.EventId} ignorado: {reason}."
            : $"Evento {response.EventId} ignorado: {reason} ({actor} no tiene permiso de escritura en {response.Repository}).";
    }

    private sealed record FixResponse(Guid EventId, bool Created, string? Status, string? Reason, string? Actor, string? Repository);

    private sealed record FixProblem(string? Title, string? Detail, string? InstanceStatus);
}
