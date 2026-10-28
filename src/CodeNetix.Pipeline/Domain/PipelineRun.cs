using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Domain;

/// <summary>
/// Agregado Ejecución (AD-2): único dueño del Estado. Solo sus métodos cambian <see cref="State"/> según la
/// tabla de transiciones de AD-2; una transición ilegal lanza <see cref="InvalidRunTransitionException"/>.
/// Cada método devuelve el <see cref="StateChanged"/> resultante, que quien lo invoca (el orquestador) persiste
/// en el mismo <c>SaveChanges</c> que el cambio de Estado.
/// </summary>
internal sealed class PipelineRun
{
    private static readonly RunState[] Cancellable =
        [RunState.Received, RunState.Analyzing, RunState.Coding, RunState.Reviewing];

    /// <summary>Para EF Core.</summary>
    private PipelineRun()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>8 caracteres hexadecimales en minúsculas, solo para mostrar o nombrar (<see cref="ShortIdOf"/>).</summary>
    public string ShortId { get; private set; } = string.Empty;

    public Guid EventId { get; private set; }

    public string ObjectKey { get; private set; } = string.Empty;

    public RunState State { get; private set; }

    public FailureReason? FailureReason { get; private set; }

    public string? FailureDetail { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Commit base del repositorio (AD-16 paso 1); <c>null</c> hasta clonarlo.</summary>
    public string? BaseSha { get; private set; }

    /// <summary>
    /// Instantánea JSON de la <see cref="EffectiveConfig"/> (AD-10); <c>null</c> hasta resolverla. Se fija una
    /// sola vez y no cambia después.
    /// </summary>
    public string? EffectiveConfigJson { get; private set; }

    /// <summary>Registra el commit base. Solo en <c>Received</c> y una sola vez.</summary>
    public RepositoryPrepared RecordBaseSha(string baseSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseSha);
        if (State != RunState.Received || BaseSha is not null)
        {
            throw new InvalidRunTransitionException(Id, State, nameof(RecordBaseSha));
        }

        BaseSha = baseSha;
        return new RepositoryPrepared(baseSha);
    }

    /// <summary>Congela la configuración efectiva. Solo en <c>Received</c>, tras <see cref="RecordBaseSha"/> y una sola vez.</summary>
    public RepoConfigResolved FreezeConfiguration(EffectiveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (State != RunState.Received || BaseSha is null || EffectiveConfigJson is not null)
        {
            throw new InvalidRunTransitionException(Id, State, nameof(FreezeConfiguration));
        }

        EffectiveConfigJson = EffectiveConfigSerializer.Serialize(config);
        return new RepoConfigResolved(config.Source);
    }

    /// <summary>La configuración congelada, o <c>null</c> si todavía no se ha resuelto.</summary>
    public EffectiveConfig? GetEffectiveConfig() =>
        EffectiveConfigJson is null ? null : EffectiveConfigSerializer.Deserialize(EffectiveConfigJson);

    /// <summary>
    /// Nueva Ejecución en <see cref="RunState.Received"/> con sus dos primeros eventos:
    /// <see cref="RunCreated"/> y <see cref="StateChanged"/> con <c>from</c> nulo.
    /// </summary>
    public static (PipelineRun Run, RunCreated Created, StateChanged Initial) Create(Guid eventId, string objectKey, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var id = Guid.CreateVersion7(now);
        var run = new PipelineRun
        {
            Id = id,
            ShortId = ShortIdOf(id),
            EventId = eventId,
            ObjectKey = objectKey,
            State = RunState.Received,
            CreatedAt = now.ToUniversalTime(),
            UpdatedAt = now.ToUniversalTime(),
        };

        return (run, new RunCreated(eventId, objectKey), new StateChanged(null, RunState.Received));
    }

    /// <summary>
    /// Los 8 últimos caracteres hexadecimales del id. En un GUID v7 los 8 primeros son la marca de tiempo
    /// (iguales para todas las Ejecuciones creadas en el mismo minuto); los últimos son aleatorios.
    /// </summary>
    public static string ShortIdOf(Guid id) => RunShortId.Of(id);

    public StateChanged StartAnalysis() => Move(nameof(StartAnalysis), RunState.Analyzing, RunState.Received);

    public StateChanged StartCoding() => Move(nameof(StartCoding), RunState.Coding, RunState.Analyzing);

    public StateChanged StartReview() => Move(nameof(StartReview), RunState.Reviewing, RunState.Coding);

    /// <summary>Ronda de revisión: <c>Reviewing</c> → <c>Coding</c>.</summary>
    public StateChanged StartReviewRound() => Move(nameof(StartReviewRound), RunState.Coding, RunState.Reviewing);

    public StateChanged StartDeciding() => Move(nameof(StartDeciding), RunState.Deciding, RunState.Reviewing);

    public StateChanged CompletePullRequest() => Move(nameof(CompletePullRequest), RunState.PullRequestCreated, RunState.Deciding);

    /// <summary>Desde cualquier Estado no terminal.</summary>
    public StateChanged Fail(FailureReason reason, string? detail)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "FailureReason desconocido.");
        }

        if (State.IsTerminal())
        {
            throw new InvalidRunTransitionException(Id, State, nameof(Fail));
        }

        var from = State;
        State = RunState.Failed;
        FailureReason = reason;
        FailureDetail = detail;
        return new StateChanged(from, RunState.Failed, reason, detail);
    }

    /// <summary>Desde todos los Estados salvo <c>Deciding</c> y los terminales.</summary>
    public StateChanged Cancel() => Move(nameof(Cancel), RunState.Cancelled, Cancellable);

    /// <summary>Lo llama el registro de eventos al persistir una transición.</summary>
    internal void Touch(DateTimeOffset at) => UpdatedAt = at.ToUniversalTime();

    /// <summary>Sustituye el detalle por su versión redactada (no cambia el Estado).</summary>
    internal void RedactFailureDetail(ISecretRedactor redactor)
    {
        if (FailureDetail is not null)
        {
            FailureDetail = redactor.Redact(FailureDetail);
        }
    }

    private StateChanged Move(string method, RunState to, params RunState[] from)
    {
        if (!from.Contains(State))
        {
            throw new InvalidRunTransitionException(Id, State, method);
        }

        var previous = State;
        State = to;
        return new StateChanged(previous, to);
    }
}

/// <summary>JSON de la instantánea <see cref="EffectiveConfig"/> en <c>run_runs.effective_config_json</c> (camelCase).</summary>
internal static class EffectiveConfigSerializer
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string Serialize(EffectiveConfig config) => System.Text.Json.JsonSerializer.Serialize(config, Options);

    public static EffectiveConfig Deserialize(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<EffectiveConfig>(json, Options)
        ?? throw new System.Text.Json.JsonException("EffectiveConfig nula.");
}

/// <summary>JSON de una <see cref="Spec"/> en <c>run_specs.spec_json</c> (camelCase).</summary>
internal static class SpecSerializer
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string Serialize(Spec spec) => System.Text.Json.JsonSerializer.Serialize(spec, Options);

    public static Spec Deserialize(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<Spec>(json, Options)
        ?? throw new System.Text.Json.JsonException("Spec nula.");
}

/// <summary>Se ha invocado un método de <see cref="PipelineRun"/> no permitido en su Estado actual (AD-2).</summary>
internal sealed class InvalidRunTransitionException(Guid runId, RunState state, string method)
    : InvalidOperationException($"Transición ilegal en la Ejecución {runId}: {method} no está permitido en el Estado {state}.")
{
    public Guid RunId { get; } = runId;

    public RunState State { get; } = state;

    public string Method { get; } = method;
}
