using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.ReceiveWebhook;
using CodeNetix.Host.Secrets;
using CodeNetix.Sandbox.Gateway;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Logging.Console;

namespace CodeNetix.Host;

/// <summary>
/// <c>codenetix action</c> (AD-12, FR-25): una Ejecución dentro de un job de GitHub Actions, sin Panel y con SQLite
/// temporal. Host lee el entorno del runner (<c>GITHUB_EVENT_NAME</c>, <c>GITHUB_EVENT_PATH</c>, <c>GITHUB_REPOSITORY</c>,
/// <c>GITHUB_RUN_ID</c>, <c>GITHUB_RUN_ATTEMPT</c>, <c>GITHUB_TOKEN</c>, <c>GITHUB_API_URL</c>, <c>GITHUB_SERVER_URL</c>);
/// Ingestion normaliza el evento y autoriza al <c>sender</c> (FR-26); el Dispatcher orquesta la Ejecución con la misma
/// composición que <c>serve</c> (<see cref="CodeNetixComposition"/>: Pipeline, Sandbox, Egress Gateway y
/// <see cref="StartupReconciler"/>), y este modo vuelca sus <c>RunEvent</c> a stdout. Kestrel solo escucha el Gateway.
/// Códigos: 0 (PR creado o nada que hacer), 1 (fallo, ignorado, GitHub caído, diagnóstico, interrupción), 2 (uso).
/// </summary>
internal static class ActionMode
{
    public const string Usage =
        "Uso: codenetix action   (dentro de un job de GitHub Actions: lee GITHUB_EVENT_NAME, GITHUB_EVENT_PATH y GITHUB_TOKEN)";

    public const string LabelKey = InstanceOptions.SectionName + ":Action:Label";

    public const string DefaultLabel = "codenetix";

    /// <summary>Identidad de un <c>GITHUB_TOKEN</c> (token de instalación: no puede leer <c>GET /user</c>).</summary>
    public const string ActionsBotLogin = "github-actions[bot]";

    public const string TokenKey = InstanceOptions.SectionName + ":GitHub:Token";
    public const string LoginKey = InstanceOptions.SectionName + ":GitHub:Login";
    public const string ApiUrlKey = InstanceOptions.SectionName + ":GitHub:ApiUrl";
    public const string CloneBaseUrlKey = InstanceOptions.SectionName + ":GitHub:CloneBaseUrl";
    public const string DataDirectoryKey = InstanceOptions.SectionName + ":Persistence:DataDirectory";

    /// <summary>GitHub limita el payload de un webhook a 25 MB; un archivo mayor no es un evento legítimo.</summary>
    public const long MaxEventFileBytes = 25L * 1024 * 1024;

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Plazo máximo esperando a que el Dispatcher asigne una Ejecución a un Evento aceptado.</summary>
    public static readonly TimeSpan DefaultDispatchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Margen de cortesía si la Ejecución ya está en Estado terminal (p. ej. terminó en otro proceso) pero no ha
    /// llegado el aviso de <see cref="IRunCompletionObserver"/> en este proceso.
    /// </summary>
    public static readonly TimeSpan DefaultTerminalGraceTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task<int> RunAsync(string[] args) => RunAsync(args, Console.Out, Console.Error, hooks: null, CancellationToken.None);

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        ActionModeHooks? hooks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.RemoveAppSettingsSources();
        hooks?.ConfigureConfiguration?.Invoke(builder.Configuration);

        // Con GITHUB_ACTIONS=true el runner interpreta "::comando::" en la salida: se desactiva con un token aleatorio
        // para que nada de lo que imprima el repositorio (salida del Sandbox, texto de la issue) sea un comando de workflow.
        var stopToken = string.Equals(builder.Configuration["GITHUB_ACTIONS"], "true", StringComparison.OrdinalIgnoreCase)
            ? Guid.NewGuid().ToString("N")
            : null;
        if (stopToken is not null)
        {
            await stdout.WriteLineAsync($"::stop-commands::{stopToken}");
        }

        string? ownedDataDirectory = null;
        try
        {
            if (args.Length > 0)
            {
                await stderr.WriteLineAsync(Usage);
                await stderr.WriteLineAsync();
                await stderr.WriteLineAsync(CodeNetixCommandLine.Usage);
                return CodeNetixCommandLine.ExitUsage;
            }

            return await RunCoreAsync(builder, stdout, stderr, hooks, dir => ownedDataDirectory = dir, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var redactor = new SecretRedactor(builder.Configuration);
            await stderr.WriteLineAsync(redactor.Redact(InterruptedMessage));
            return CodeNetixCommandLine.ExitFailure;
        }
        catch (Exception ex)
        {
            var redactor = new SecretRedactor(builder.Configuration);
            await stderr.WriteLineAsync(redactor.Redact($"Error inesperado en codenetix action: {ex.Message}"));
            return CodeNetixCommandLine.ExitFailure;
        }
        finally
        {
            DeleteDataDirectory(ownedDataDirectory);
            if (stopToken is not null)
            {
                await stdout.WriteLineAsync($"::{stopToken}::");
            }
        }
    }

    private static async Task<int> RunCoreAsync(
        WebApplicationBuilder builder,
        TextWriter stdout,
        TextWriter stderr,
        ActionModeHooks? hooks,
        Action<string> registerOwnedDataDirectory,
        CancellationToken cancellationToken)
    {
        var configuration = builder.Configuration;
        try
        {
            configuration.AddInstanceConfigFile();
        }
        catch (InstanceConfigurationException ex)
        {
            await stderr.WriteLineAsync(new SecretRedactor(configuration).Redact(ex.Message));
            return CodeNetixCommandLine.ExitFailure;
        }

        // 1) El evento del runner.
        var eventName = configuration["GITHUB_EVENT_NAME"]?.Trim();
        var eventPath = configuration["GITHUB_EVENT_PATH"]?.Trim();
        if (string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(eventPath))
        {
            await stderr.WriteLineAsync(
                "codenetix action solo se ejecuta dentro de un job de GitHub Actions: faltan GITHUB_EVENT_NAME o GITHUB_EVENT_PATH.");
            await stderr.WriteLineAsync(Usage);
            return CodeNetixCommandLine.ExitUsage;
        }

        string eventJson;
        try
        {
            var file = new FileInfo(eventPath);
            if (file.Exists && file.Length > MaxEventFileBytes)
            {
                await stderr.WriteLineAsync($"El evento de GITHUB_EVENT_PATH ('{eventPath}') supera {MaxEventFileBytes / (1024 * 1024)} MB.");
                await stderr.WriteLineAsync(Usage);
                return CodeNetixCommandLine.ExitUsage;
            }

            eventJson = await File.ReadAllTextAsync(eventPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await stderr.WriteLineAsync($"No se puede leer el evento de GITHUB_EVENT_PATH ('{eventPath}'): {ex.Message}");
            await stderr.WriteLineAsync(Usage);
            return CodeNetixCommandLine.ExitUsage;
        }

        // 2) Valores del runner para lo que la Configuración de instancia no fije (el entorno CODENETIX__* manda).
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var tokenFromRunner = false;
        if (string.IsNullOrWhiteSpace(configuration[TokenKey]) && !string.IsNullOrWhiteSpace(configuration["GITHUB_TOKEN"]))
        {
            overrides[TokenKey] = configuration["GITHUB_TOKEN"]!.Trim();
            tokenFromRunner = true;
        }

        if (tokenFromRunner && string.IsNullOrWhiteSpace(configuration[LoginKey]))
        {
            overrides[LoginKey] = ActionsBotLogin;
        }

        CopyIfMissing(configuration, overrides, ApiUrlKey, "GITHUB_API_URL");
        CopyIfMissing(configuration, overrides, CloneBaseUrlKey, "GITHUB_SERVER_URL");

        if (string.IsNullOrWhiteSpace(configuration[DataDirectoryKey]))
        {
            var tempRoot = string.IsNullOrWhiteSpace(configuration["RUNNER_TEMP"]) ? Path.GetTempPath() : configuration["RUNNER_TEMP"]!;
            var ownedDataDirectory = Path.Combine(tempRoot, "codenetix-action-" + Guid.NewGuid().ToString("N"));
            registerOwnedDataDirectory(ownedDataDirectory);
            overrides[DataDirectoryKey] = ownedDataDirectory;
        }

        if (overrides.Count > 0)
        {
            configuration.AddInMemoryCollection(overrides);
        }

        // Desde aquí la configuración incluye el token del runner: todo lo que se imprime pasa por el redactor.
        var redactor = new SecretRedactor(configuration);
        var output = new ActionOutput(stdout, stderr, redactor);

        // 3) La misma composición que serve, sin Panel, API, hub ni webhooks: Kestrel solo para el Egress Gateway (:8080).
        builder.WebHost.UseUrls(ListenUrls(configuration));
        builder.AddCodeNetixServices();
        builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddSingleton<ActionRunCompletion>();
        builder.Services.AddSingleton<IRunCompletionObserver>(sp => sp.GetRequiredService<ActionRunCompletion>());
        hooks?.ConfigureBuilder?.Invoke(builder);

        WebApplication app;
        try
        {
            app = builder.Build();
        }
        catch (Exception ex)
        {
            await output.ErrorAsync($"CodeNetix no ha podido arrancar: {ex.Message}");
            return CodeNetixCommandLine.ExitFailure;
        }

        app.UseMiddleware<WebhookPortGuardMiddleware>();
        app.MapLlmGateway();

        var started = false;
        try
        {
            // 4) Normalizar el evento (Ingestion, sin E/S): si no aplica, se sale sin arrancar nada (ni Docker ni GitHub).
            var ingestion = app.Services.GetRequiredService<IGitHubActionEventIngestion>();
            var label = string.IsNullOrWhiteSpace(configuration[LabelKey]) ? DefaultLabel : configuration[LabelKey]!.Trim();
            var parse = ingestion.Parse(new GitHubActionEventInput(
                eventName,
                eventJson,
                configuration["GITHUB_REPOSITORY"],
                configuration["GITHUB_RUN_ID"],
                configuration["GITHUB_RUN_ATTEMPT"],
                label));
            switch (parse.Kind)
            {
                case GitHubActionEventKind.NotApplicable:
                    await output.WriteAsync($"CodeNetix action: nada que hacer: {parse.Reason}");
                    return CodeNetixCommandLine.ExitOk;
                case GitHubActionEventKind.Invalid:
                    await output.ErrorAsync($"Evento de GitHub Actions no válido: {parse.Reason}");
                    await output.ErrorAsync(Usage);
                    return CodeNetixCommandLine.ExitUsage;
            }

            var trigger = parse.Trigger!;
            var repository = $"{trigger.Owner}/{trigger.Repo}";
            await output.WriteAsync(
                $"CodeNetix action: issue {repository}#{trigger.IssueNumber} etiquetada '{trigger.Label}' por {trigger.Sender}.");

            // 5) Arranque AD-18 (migraciones → salud → reconciliación → limpieza → Ready/Diagnostic) sobre la SQLite temporal.
            try
            {
                started = true;
                await app.StartAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await output.ErrorAsync(InterruptedMessage);
                return CodeNetixCommandLine.ExitFailure;
            }
            catch (Exception ex)
            {
                await output.ErrorAsync($"CodeNetix no ha podido arrancar: {ex.Message}");
                return CodeNetixCommandLine.ExitFailure;
            }

            var health = app.Services.GetRequiredService<InstanceHealth>();
            var (status, reasons) = health.Read();
            if (status != InstanceHealthStatus.Ready)
            {
                await output.ErrorAsync($"CodeNetix no puede procesar trabajo (modo diagnóstico, {reasons.Count} motivo(s)):");
                foreach (var reason in reasons)
                {
                    await output.ErrorAsync($"  - {reason}");
                }

                return CodeNetixCommandLine.ExitFailure;
            }

            var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
            try
            {
                return await SubmitAndFollowAsync(
                    app.Services,
                    ingestion,
                    trigger,
                    output,
                    hooks?.PollInterval ?? DefaultPollInterval,
                    hooks?.DispatchTimeout ?? DefaultDispatchTimeout,
                    hooks?.TerminalGraceTimeout ?? DefaultTerminalGraceTimeout,
                    linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                await output.ErrorAsync(InterruptedMessage);
                return CodeNetixCommandLine.ExitFailure;
            }
        }
        finally
        {
            if (started)
            {
                using var stopTimeout = new CancellationTokenSource(StopTimeout);
                try
                {
                    await app.StopAsync(stopTimeout.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or AggregateException or InvalidOperationException)
                {
                    // Mejor esfuerzo: el proceso termina igualmente.
                }
            }

            await app.DisposeAsync();
        }
    }

    public const string InterruptedMessage =
        "Ejecución interrumpida: el job se ha cancelado o el proceso se está deteniendo.";

    /// <summary>Solo el Egress Gateway LLM (<c>CODENETIX__Sandbox__LlmGatewayPort</c>, 8080): nunca :5000 ni :5001.</summary>
    internal static string[] ListenUrls(IConfiguration configuration) =>
        [$"http://+:{CodeNetixComposition.LlmGatewayPort(configuration)}"];

    private static async Task<int> SubmitAndFollowAsync(
        IServiceProvider services,
        IGitHubActionEventIngestion ingestion,
        GitHubActionTrigger trigger,
        ActionOutput output,
        TimeSpan pollInterval,
        TimeSpan dispatchTimeout,
        TimeSpan terminalGraceTimeout,
        CancellationToken cancellationToken)
    {
        var repository = $"{trigger.Owner}/{trigger.Repo}";

        // 6) Ingestion: permiso de escritura del sender (FR-26) y persistencia; el Dispatcher crea la Ejecución.
        var submitted = await ingestion.SubmitAsync(trigger, cancellationToken);
        if (submitted.Outcome == ManualEventOutcome.Unavailable || submitted.Event is null)
        {
            await output.ErrorAsync($"No se puede autorizar a {trigger.Sender} en {repository}: {submitted.Reason ?? "GitHub no responde"}.");
            return CodeNetixCommandLine.ExitFailure;
        }

        var info = submitted.Event;
        if (info.Status == EventStatus.Ignored)
        {
            await output.ErrorAsync(
                $"Evento {info.Id} ignorado: {info.IgnoredReason ?? "no autorizado"} ({trigger.Sender} no tiene permiso de escritura en {repository}).");
            return CodeNetixCommandLine.ExitFailure;
        }

        await output.WriteAsync($"Evento {info.Id} registrado.");

        // 7) Esperar a que el Dispatcher cree la Ejecución (o ignore el Evento), con límite para evitar espera infinita.
        var events = services.GetRequiredService<IEventReader>();
        var dispatchDeadline = Stopwatch.StartNew();
        Guid runId;
        while (true)
        {
            var status = await events.FindAsync(info.Id, cancellationToken);
            if (status is null)
            {
                await output.ErrorAsync($"Evento {info.Id} no encontrado al esperar su Ejecución.");
                return CodeNetixCommandLine.ExitFailure;
            }

            if (status.Status == EventStatus.Ignored)
            {
                await output.ErrorAsync($"Evento {info.Id} ignorado: {status.Reason ?? "sin motivo"}.");
                return CodeNetixCommandLine.ExitFailure;
            }

            if (status.RunId is { } id && id != Guid.Empty)
            {
                runId = id;
                break;
            }

            if (dispatchDeadline.Elapsed >= dispatchTimeout)
            {
                await output.ErrorAsync($"Tiempo agotado esperando a que el Dispatcher cree una Ejecución para el Evento {info.Id}.");
                return CodeNetixCommandLine.ExitFailure;
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        await output.WriteAsync($"Ejecución {runId}:");

        // 8) Volcar sus RunEvent por orden de seq hasta que el orquestador termine con ella (avisos y Sandbox incluidos).
        var reader = services.GetRequiredService<IRunReader>();
        var completion = services.GetRequiredService<ActionRunCompletion>().WhenCompleted(runId);
        long afterSeq = 0;
        string? pullRequestUrl = null;
        Stopwatch? terminalGrace = null;

        async Task DrainEventsAsync()
        {
            foreach (var runEvent in await reader.GetEventsAsync(runId, afterSeq, cancellationToken))
            {
                if (runEvent.Seq <= afterSeq)
                {
                    continue;
                }

                afterSeq = runEvent.Seq;
                if (runEvent is PullRequestOpened opened)
                {
                    pullRequestUrl = opened.Url;
                }

                var element = JsonSerializer.SerializeToElement(runEvent, Json);
                foreach (var line in FormatEvent(element, runEvent.Seq))
                {
                    await output.WriteAsync(line);
                }
            }
        }

        while (true)
        {
            var finished = completion.IsCompleted;
            await DrainEventsAsync();
            if (finished)
            {
                break;
            }

            // Si la Ejecución ya está en Estado terminal (p. ej. terminó en otro proceso) y no llega el aviso
            // en este proceso tras el margen de cortesía, salimos en lugar de esperar indefinidamente.
            var current = await reader.FindAsync(runId.ToString("D"), cancellationToken);
            if (current is null || current.State.IsTerminal())
            {
                terminalGrace ??= Stopwatch.StartNew();
                if (terminalGrace.Elapsed >= terminalGraceTimeout)
                {
                    break;
                }
            }
            else
            {
                terminalGrace = null;
            }

            await Task.WhenAny(completion, Task.Delay(pollInterval, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Drenaje final tras el fin de la orquestación para no perder eventos tardíos (IssueCommented, SandboxReleased).
        await DrainEventsAsync();

        // 9) Resultado y código de salida.
        var summary = await reader.FindAsync(runId.ToString("D"), cancellationToken);
        switch (summary?.State)
        {
            case RunState.PullRequestCreated:
                await output.WriteAsync(pullRequestUrl is null ? "Resultado: PullRequestCreated" : $"Resultado: PullRequestCreated {pullRequestUrl}");
                return CodeNetixCommandLine.ExitOk;
            case RunState.Failed:
                var detail = string.IsNullOrWhiteSpace(summary.FailureDetail) ? string.Empty : $": {summary.FailureDetail}";
                await output.WriteAsync($"Resultado: Failed ({summary.FailureReason}{detail})");
                return CodeNetixCommandLine.ExitFailure;
            case RunState.Cancelled:
                await output.WriteAsync("Resultado: Cancelled");
                return CodeNetixCommandLine.ExitFailure;
            default:
                await output.ErrorAsync($"La Ejecución {runId} ha terminado sin Estado terminal ({summary?.State.ToString() ?? "desconocido"}).");
                return CodeNetixCommandLine.ExitFailure;
        }
    }

    /// <summary>
    /// Una o varias líneas por <c>RunEvent</c>: el formato de <c>fix --follow</c> y, además, el texto de
    /// <c>OutputAppended</c> (ya redactado por el sumidero) y los tipos que en el log del job aportan contexto.
    /// </summary>
    internal static IEnumerable<string> FormatEvent(JsonElement item, long seq)
    {
        var type = GetString(item, "type");
        switch (type)
        {
            case "OutputAppended":
            {
                var stream = GetString(item, "stream") ?? "stdout";
                var text = (GetString(item, "text") ?? string.Empty).ReplaceLineEndings("\n").TrimEnd('\n');
                foreach (var line in text.Split('\n'))
                {
                    yield return $"[{seq}] {stream} | {line}";
                }

                yield break;
            }

            case "OutputTruncated":
                yield return $"[{seq}] OutputTruncated (salida de la Etapa recortada a {GetNumber(item, "maxBytes")} bytes)";
                yield break;
            case "TokensConsumed":
                yield return $"[{seq}] TokensConsumed {GetString(item, "role")} {GetString(item, "provider")}/{GetString(item, "model")} ({GetNumber(item, "inputTokens")} → {GetNumber(item, "outputTokens")} tokens)";
                yield break;
            case "IssueCommented":
                yield return $"[{seq}] IssueCommented {GetString(item, "kind")} {GetString(item, "commentUrl")}".TrimEnd();
                yield break;
            case "NotificationFailed":
                yield return $"[{seq}] NotificationFailed {GetString(item, "kind")}: {GetString(item, "error")}";
                yield break;
            case "EgressBlocked":
                yield return $"[{seq}] EgressBlocked {GetString(item, "target")}: {GetString(item, "reason")}";
                yield break;
            case "CodingAttemptFailed":
                yield return $"[{seq}] CodingAttemptFailed intento {GetNumber(item, "attempt")}: {GetString(item, "reason")}";
                yield break;
            case "AnalysisRejected":
                yield return $"[{seq}] AnalysisRejected {GetString(item, "category")}";
                yield break;
            default:
                yield return FixMode.FormatEvent(item, seq);
                yield break;
        }
    }

    private static string GetNumber(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "?";

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void CopyIfMissing(IConfiguration configuration, Dictionary<string, string?> overrides, string key, string runnerKey)
    {
        if (string.IsNullOrWhiteSpace(configuration[key]) && !string.IsNullOrWhiteSpace(configuration[runnerKey]))
        {
            overrides[key] = configuration[runnerKey]!.Trim();
        }
    }

    private static void DeleteDataDirectory(string? path)
    {
        if (path is null)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(path))
            {
                // Sin seguir enlaces: solo se quita el atributo de solo lectura (objetos de .git en Windows) de lo propio.
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                    }
                }

                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mejor esfuerzo: el runner es efímero.
        }
    }

    /// <summary>Salida del modo: stdout para el progreso y stderr para los errores, siempre redactadas (AD-13).</summary>
    private sealed class ActionOutput(TextWriter stdout, TextWriter stderr, SecretRedactor redactor)
    {
        public Task WriteAsync(string line) => stdout.WriteLineAsync(redactor.Redact(line));

        public Task ErrorAsync(string line) => stderr.WriteLineAsync(redactor.Redact(line));
    }
}

/// <summary>Puntos de extensión de <see cref="ActionMode"/> para las pruebas.</summary>
internal sealed class ActionModeHooks
{
    /// <summary>Antes de leer nada: p. ej. sustituir el entorno del proceso por valores en memoria.</summary>
    public Action<ConfigurationManager>? ConfigureConfiguration { get; init; }

    /// <summary>Tras la composición y antes de construir: servicios falsos, URL de Kestrel…</summary>
    public Action<WebApplicationBuilder>? ConfigureBuilder { get; init; }

    public TimeSpan? PollInterval { get; init; }

    public TimeSpan? DispatchTimeout { get; init; }

    public TimeSpan? TerminalGraceTimeout { get; init; }

    /// <summary>Sustituye las fuentes de entorno y de línea de comandos por <paramref name="values"/> (pruebas herméticas).</summary>
    public static void ReplaceEnvironment(ConfigurationManager configuration, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var sources = ((IConfigurationBuilder)configuration).Sources;
        for (var i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource or CommandLineConfigurationSource)
            {
                sources.RemoveAt(i);
            }
        }

        configuration.AddInMemoryCollection(values);
    }
}

/// <summary>Fin de cada orquestación (<see cref="IRunCompletionObserver"/>) para que <c>action</c> sepa cuándo salir.</summary>
internal sealed class ActionRunCompletion : IRunCompletionObserver
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _runs = new();

    public void OnOrchestrationCompleted(Guid runId) => Get(runId).TrySetResult();

    public Task WhenCompleted(Guid runId) => Get(runId).Task;

    private TaskCompletionSource Get(Guid runId) =>
        _runs.GetOrAdd(runId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}
