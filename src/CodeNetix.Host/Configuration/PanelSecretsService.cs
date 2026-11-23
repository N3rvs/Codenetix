using CodeNetix.Agents;
using CodeNetix.Contracts;
using CodeNetix.GitHub;
using CodeNetix.Host.Secrets;
using CodeNetix.Sandbox;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Configuration;

/// <summary>Resultado de <see cref="PanelSecretsService.UpdateAsync"/>.</summary>
internal enum PanelSecretsUpdateOutcome
{
    Saved,

    /// <summary>Petición sin claves o con un formato imposible (<c>400</c>).</summary>
    Invalid,

    /// <summary>Alguna clave la define el entorno o el YAML (<c>409</c>).</summary>
    ManagedByConfiguration,

    /// <summary>Alguna clave no pasa la comprobación o empeoraría la configuración (<c>422</c>).</summary>
    Rejected,

    /// <summary>La instancia aún está arrancando (<c>503</c>).</summary>
    NotReady,

    /// <summary>No se pudo escribir el archivo cifrado (<c>500</c>).</summary>
    StorageFailed,
}

/// <param name="Errors">Mensaje por clave (nunca el valor).</param>
/// <param name="Checks">Resultado de cada comprobación remota superada.</param>
internal sealed record PanelSecretsUpdateResult(
    PanelSecretsUpdateOutcome Outcome,
    string? Detail,
    IReadOnlyDictionary<PanelSecretId, string> Errors,
    IReadOnlyDictionary<PanelSecretId, string> Checks);

/// <summary>Estado de una clave para el Panel: nunca el valor, solo origen y una pista enmascarada.</summary>
internal sealed record PanelSecretState(PanelSecretId Id, SecretSource Source, string? Hint);

/// <summary>
/// Claves desde el Panel (historia 8.1): estado, validación, guardado cifrado y aplicación en caliente.
/// <list type="number">
/// <item>Formato (ASCII imprimible 0x21–0x7E, 8–1024); solo entonces los valores se registran en
/// <see cref="SecretRedactor"/>, antes de cualquier E/S.</item>
/// <item>Origen: una clave del entorno/YAML no se toca.</item>
/// <item>Comprobación remota en paralelo (<see cref="SecretValidator"/>); si alguna falla, no se guarda nada.</item>
/// <item>La combinación nueva no puede añadir motivos de <c>Diagnostic</c> (p. ej. dos Proveedores sin Anthropic).</item>
/// <item>Guardado cifrado (<see cref="PanelSecretStore"/>), invalidación de opciones, <c>Coder</c> del Gateway y, si la
/// instancia estaba en <c>Diagnostic</c>, reevaluación de salud (configuración, Docker y motivos de arranque) con
/// plazo propio, sin el token de la petición.</item>
/// </list>
/// Las actualizaciones se serializan. Nunca registra valores: solo qué claves cambian.
/// </summary>
internal sealed partial class PanelSecretsService(
    PanelSecretStore store,
    SecretValidator validator,
    SecretRedactor redactor,
    InstanceHealth health,
    ISandboxHealthCheck sandboxHealth,
    IConfiguration configuration,
    IOptionsFactory<InstanceOptions> instanceFactory,
    IOptionsMonitor<InstanceOptions> instanceMonitor,
    IOptionsMonitorCache<InstanceOptions> instanceCache,
    IOptionsMonitor<GitHubOptions> gitHubMonitor,
    IOptionsMonitorCache<GitHubOptions> gitHubCache,
    IOptionsMonitorCache<AgentsOptions> agentsCache,
    IOptionsMonitor<SandboxOptions> sandboxMonitor,
    IOptions<SandboxOptions> sandboxOptions,
    ILogger<PanelSecretsService> logger) : IDisposable
{
    public const int MinLength = 8;
    public const int MaxLength = 1024;

    public const string InvalidCharactersMessage =
        "La clave contiene caracteres no válidos (¿espacios o caracteres invisibles al copiarla?).";

    /// <summary>Plazo de la reevaluación de salud tras guardar (independiente de la petición).</summary>
    public static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    /// <summary>Estado de las tres claves (origen y pista; nunca el valor).</summary>
    public IReadOnlyList<PanelSecretState> GetStates()
    {
        var panel = store.Current;
        var instance = instanceMonitor.CurrentValue;
        return [.. PanelSecretCatalog.All.Select(id =>
        {
            var source = PanelSecretCatalog.SourceOf(configuration, id, panel);
            var value = source switch
            {
                SecretSource.None => null,
                SecretSource.Panel => panel.Get(id),
                _ => EffectiveValue(instance, id),
            };
            return new PanelSecretState(id, source, value is null ? null : PanelSecretCatalog.Hint(value));
        })];
    }

    /// <summary>Aviso si el archivo guardado no se pudo leer (sin valores).</summary>
    public string? Notice => store.LoadError;

    public async Task<PanelSecretsUpdateResult> UpdateAsync(IReadOnlyDictionary<PanelSecretId, string?> requested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UpdateCoreAsync(requested, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PanelSecretsUpdateResult> UpdateCoreAsync(IReadOnlyDictionary<PanelSecretId, string?> requested, CancellationToken cancellationToken)
    {
        var changes = requested
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value!.Trim());

        if (changes.Count == 0)
        {
            return Failure(PanelSecretsUpdateOutcome.Invalid, "Indica al menos una clave para guardar.");
        }

        if (health.Status == InstanceHealthStatus.Starting)
        {
            return Failure(PanelSecretsUpdateOutcome.NotReady, "CodeNetix está arrancando; vuelve a intentarlo en unos segundos.");
        }

        var formatErrors = new Dictionary<PanelSecretId, string>();
        foreach (var (id, value) in changes)
        {
            if (FormatError(id, value) is { } error)
            {
                formatErrors[id] = error;
            }
        }

        if (formatErrors.Count > 0)
        {
            return Failure(PanelSecretsUpdateOutcome.Invalid, "Revisa el formato de las claves: no se ha guardado nada.", formatErrors);
        }

        // Solo valores con forma de clave entran en el redactor (antes de cualquier E/S): una palabra corriente como
        // "main" no debe acabar enmascarada en todas las respuestas y logs.
        redactor.AddSecrets(changes.Values);

        var managed = changes.Keys.Where(id => PanelSecretCatalog.IsSetByConfiguration(configuration, id)).ToList();
        if (managed.Count > 0)
        {
            var errors = managed.ToDictionary(
                id => id,
                id => $"Está configurada fuera del Panel ({PanelSecretCatalog.EnvironmentVariable(id)} o el archivo de instancia) y tiene prioridad: cámbiala allí.");
            return Failure(PanelSecretsUpdateOutcome.ManagedByConfiguration, "Alguna clave está configurada por variable de entorno o por el archivo de instancia: no se ha guardado nada.", errors);
        }

        // Comprobación remota de todas a la vez: todo o nada.
        var instance = instanceMonitor.CurrentValue;
        var checks = await Task.WhenAll(changes.Select(async p => (Id: p.Key, Check: await CheckAsync(p.Key, p.Value, instance, cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);
        var failed = checks.Where(c => !c.Check.Valid).ToDictionary(c => c.Id, c => c.Check.Message);
        if (failed.Count > 0)
        {
            LogValidationFailed(Describe(failed.Keys));
            return Failure(PanelSecretsUpdateOutcome.Rejected, "Alguna clave no ha pasado la comprobación: no se ha guardado nada.", failed);
        }

        // La combinación nueva no puede introducir motivos de Diagnostic que antes no había.
        var next = store.Current.With(changes);
        var before = InstanceOptionsValidator.Validate(instance);
        var prospective = instanceFactory.Create(Options.DefaultName);
        PanelSecretCatalog.Apply(prospective, next, configuration);
        var introduced = InstanceOptionsValidator.Validate(prospective).Except(before, StringComparer.Ordinal).ToList();
        if (introduced.Count > 0)
        {
            return Failure(
                PanelSecretsUpdateOutcome.Rejected,
                "Con estas claves la configuración quedaría incompleta, así que no se ha guardado nada: " + string.Join(" ", introduced));
        }

        try
        {
            store.Save(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStorageFailed(store.FilePath, ex.GetType().Name);
            return Failure(
                PanelSecretsUpdateOutcome.StorageFailed,
                $"No se han podido guardar las claves en '{store.FilePath}': comprueba que el directorio de datos admite escritura.");
        }

        Apply();
        LogSaved(Describe(changes.Keys));

        // Ya guardado y aplicado: la reevaluación no depende de que el cliente siga conectado (si cerrara la pestaña, la
        // instancia se quedaría en Diagnostic con las claves ya guardadas). Plazo propio.
        using (var healthTimeout = new CancellationTokenSource(HealthCheckTimeout))
        {
            try
            {
                await ReevaluateHealthAsync(healthTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (healthTimeout.IsCancellationRequested)
            {
                LogHealthCheckTimedOut((int)HealthCheckTimeout.TotalSeconds);
            }
        }

        return new PanelSecretsUpdateResult(
            PanelSecretsUpdateOutcome.Saved,
            null,
            new Dictionary<PanelSecretId, string>(),
            checks.ToDictionary(c => c.Id, c => c.Check.Message));
    }

    /// <summary>
    /// Invalida las opciones que dependen de las claves (en orden: la Configuración de instancia antes que las derivadas) y
    /// actualiza el <c>Coder</c> del Gateway. <see cref="SandboxOptions"/> no se invalida: la instancia del monitor guarda
    /// el puerto real del proxy <c>CONNECT</c>.
    /// </summary>
    private void Apply()
    {
        instanceCache.TryRemove(Options.DefaultName);
        gitHubCache.TryRemove(Options.DefaultName);
        agentsCache.TryRemove(Options.DefaultName);

        var coder = CodeNetixComposition.ResolveSandboxCoder(instanceMonitor.CurrentValue);
        sandboxMonitor.CurrentValue.Coder = coder;
        sandboxOptions.Value.Coder = coder;
    }

    /// <summary>Solo desde <c>Diagnostic</c>: una instancia <c>Ready</c> ya tiene sus workers en marcha.</summary>
    private async Task ReevaluateHealthAsync(CancellationToken cancellationToken)
    {
        if (health.Status != InstanceHealthStatus.Diagnostic)
        {
            return;
        }

        var reasons = new List<string>(InstanceOptionsValidator.Validate(instanceMonitor.CurrentValue));
        reasons.AddRange(await sandboxHealth.CheckAsync(cancellationToken).ConfigureAwait(false));
        reasons.AddRange(health.StartupReasons);

        if (reasons.Count == 0)
        {
            health.MarkReady();
            LogReady();
            return;
        }

        health.MarkDiagnostic(reasons);
        LogStillDiagnostic(reasons.Count);
    }

    private async Task<SecretCheck> CheckAsync(PanelSecretId id, string value, InstanceOptions instance, CancellationToken cancellationToken)
    {
        if (PanelSecretCatalog.ProviderName(id) is not { } providerName)
        {
            return await validator.CheckGitHubTokenAsync(value, gitHubMonitor.CurrentValue.ApiUrl, cancellationToken).ConfigureAwait(false);
        }

        var provider = instance.Providers is { } providers && providers.TryGetValue(providerName, out var configured) ? configured : null;
        var baseUrl = AgentEndpointResolver.ResolveBaseUrl(providerName, provider, out var reason);
        return baseUrl is null
            ? new SecretCheck(false, reason!)
            : await validator.CheckProviderKeyAsync(providerName, baseUrl, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Solo ASCII imprimible (0x21–0x7E) y entre 8 y 1024 caracteres: así son los tokens y keys reales.</summary>
    internal static string? FormatError(PanelSecretId id, string value)
    {
        if (value.Any(c => c is < '!' or > '~'))
        {
            return InvalidCharactersMessage;
        }

        return value.Length is < MinLength or > MaxLength
            ? $"El {PanelSecretCatalog.Label(id)} no tiene un formato válido: debe tener entre {MinLength} y {MaxLength} caracteres."
            : null;
    }

    /// <summary>
    /// <c>true</c> si la instancia no puede trabajar porque falta el token de GitHub o la key de un Proveedor (el Panel
    /// ofrece entonces la pantalla de configuración en lugar de "define las variables y reinicia").
    /// </summary>
    public bool KeysMissing
    {
        get
        {
            var reasons = InstanceOptionsValidator.Validate(instanceMonitor.CurrentValue);
            return reasons.Contains(InstanceOptionsValidator.MissingGitHubToken, StringComparer.Ordinal)
                || reasons.Contains(InstanceOptionsValidator.MissingProviderApiKey, StringComparer.Ordinal);
        }
    }

    private static string? EffectiveValue(InstanceOptions instance, PanelSecretId id)
    {
        string? value;
        if (PanelSecretCatalog.ProviderName(id) is { } providerName)
        {
            value = instance.Providers is { } providers && providers.TryGetValue(providerName, out var provider) ? provider?.ApiKey : null;
        }
        else
        {
            value = instance.GitHub?.Token;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string Describe(IEnumerable<PanelSecretId> ids) => string.Join(", ", ids.Order().Select(PanelSecretCatalog.Label));

    private static PanelSecretsUpdateResult Failure(
        PanelSecretsUpdateOutcome outcome,
        string detail,
        IReadOnlyDictionary<PanelSecretId, string>? errors = null) =>
        new(outcome, detail, errors ?? new Dictionary<PanelSecretId, string>(), new Dictionary<PanelSecretId, string>());

    [LoggerMessage(Level = LogLevel.Information, Message = "Claves actualizadas desde el Panel: {Secrets}.")]
    private partial void LogSaved(string secrets);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claves rechazadas desde el Panel ({Secrets}): no superan la comprobación; no se ha guardado nada.")]
    private partial void LogValidationFailed(string secrets);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se han podido guardar las claves del Panel en '{Path}' ({Error}).")]
    private partial void LogStorageFailed(string path, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claves guardadas, pero la reevaluación de salud no ha terminado en {Seconds} s; el estado no cambia.")]
    private partial void LogHealthCheckTimedOut(int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuración completada desde el Panel: CodeNetix está listo.")]
    private partial void LogReady();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claves guardadas, pero CodeNetix sigue en modo diagnóstico ({Count} motivo(s)).")]
    private partial void LogStillDiagnostic(int count);
}
