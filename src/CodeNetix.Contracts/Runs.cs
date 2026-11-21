using System.Text.Json.Serialization;

namespace CodeNetix.Contracts;

/// <summary>Estado de una Ejecución (AD-2). Se serializa con estos nombres.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunState>))]
public enum RunState
{
    Received,
    Analyzing,
    Coding,
    Reviewing,
    Deciding,
    PullRequestCreated,
    Failed,
    Cancelled,
}

/// <summary>Motivo de <see cref="RunState.Failed"/> (AD-2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FailureReason>))]
public enum FailureReason
{
    InvalidRepoConfig,
    CloneFailed,
    NotAutomatable,
    SchemaInvalid,
    TestsFailed,
    ReviewRejected,
    ReviewRoundsExhausted,
    BudgetExceeded,
    Timeout,
    HostRestarted,
    StageError,
}

/// <summary>Tipo de Etapa de una Ejecución en <c>run_stages</c> (AD-11).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StageKind>))]
public enum StageKind
{
    Analysis,
    Coding,
    Testing,
    Review,
    Decision,
}

public static class RunStateExtensions
{
    /// <summary><c>PullRequestCreated</c>, <c>Failed</c> y <c>Cancelled</c>: de ellos no se sale.</summary>
    public static bool IsTerminal(this RunState state) =>
        state is RunState.PullRequestCreated or RunState.Failed or RunState.Cancelled;
}

/// <summary>
/// Evento observable de una Ejecución (AD-3): unión discriminada por <c>type</c> (verbos en pasado).
/// <see cref="Seq"/>, <see cref="RunId"/> y <see cref="At"/> los asigna el sumidero al persistirlo; los
/// productores solo construyen la parte propia de cada tipo. Un <c>RunEvent</c> nunca dispara efectos.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunCreated), nameof(RunCreated))]
[JsonDerivedType(typeof(StateChanged), nameof(StateChanged))]
[JsonDerivedType(typeof(RepositoryPrepared), nameof(RepositoryPrepared))]
[JsonDerivedType(typeof(RepoConfigResolved), nameof(RepoConfigResolved))]
[JsonDerivedType(typeof(AnalystOutputRejected), nameof(AnalystOutputRejected))]
[JsonDerivedType(typeof(SpecGenerated), nameof(SpecGenerated))]
[JsonDerivedType(typeof(AnalysisRejected), nameof(AnalysisRejected))]
[JsonDerivedType(typeof(IssueCommented), nameof(IssueCommented))]
[JsonDerivedType(typeof(NotificationFailed), nameof(NotificationFailed))]
[JsonDerivedType(typeof(SandboxCreated), nameof(SandboxCreated))]
[JsonDerivedType(typeof(SandboxReleased), nameof(SandboxReleased))]
[JsonDerivedType(typeof(TokensConsumed), nameof(TokensConsumed))]
[JsonDerivedType(typeof(EgressBlocked), nameof(EgressBlocked))]
[JsonDerivedType(typeof(OutputAppended), nameof(OutputAppended))]
[JsonDerivedType(typeof(OutputTruncated), nameof(OutputTruncated))]
[JsonDerivedType(typeof(CodingAttemptFailed), nameof(CodingAttemptFailed))]
[JsonDerivedType(typeof(ReviewerOutputRejected), nameof(ReviewerOutputRejected))]
[JsonDerivedType(typeof(VerdictRecorded), nameof(VerdictRecorded))]
[JsonDerivedType(typeof(RiskClassified), nameof(RiskClassified))]
[JsonDerivedType(typeof(PullRequestOpened), nameof(PullRequestOpened))]
public abstract record RunEvent
{
    /// <summary>Monotónico por Ejecución, empieza en 1 y no tiene huecos.</summary>
    public long Seq { get; init; }

    public Guid RunId { get; init; }

    /// <summary>Identificador de la Etapa que produjo el evento (<c>run_stages.id</c>), si aplica.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? StageId { get; init; }

    /// <summary>Instante UTC.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>La Ejecución se ha creado a partir del Evento <see cref="EventId"/>.</summary>
public sealed record RunCreated(Guid EventId, string ObjectKey) : RunEvent;

/// <summary>Transición de Estado. <see cref="From"/> es <c>null</c> solo en la creación.</summary>
public sealed record StateChanged(RunState? From, RunState To, FailureReason? FailureReason = null, string? Detail = null) : RunEvent;

/// <summary>El repositorio se ha clonado y fijado en <see cref="BaseSha"/> (AD-16 paso 1).</summary>
public sealed record RepositoryPrepared(string BaseSha) : RunEvent;

/// <summary>Se ha calculado la <see cref="EffectiveConfig"/> de la Ejecución a partir de <see cref="Source"/>.</summary>
public sealed record RepoConfigResolved(ConfigSource Source) : RunEvent;

/// <summary>El Analista ha rechazado su salida número <see cref="Attempt"/> (esquema o reglas de la Spec).</summary>
/// <param name="Error">Error de validación (nunca incluye el texto de la issue).</param>
public sealed record AnalystOutputRejected(int Attempt, string Error) : RunEvent;

/// <summary>Spec válida persistida en <c>run_specs</c> tras <see cref="Attempts"/> intentos con <see cref="Model"/>.</summary>
public sealed record SpecGenerated(int Attempts, string Model) : RunEvent;

/// <summary>
/// El Analista ha declarado la issue no automatizable (FR-10) tras <see cref="Attempts"/> intentos con <see cref="Model"/>.
/// La explicación no va aquí: está en el detalle del fallo y en el comentario de la issue.
/// </summary>
public sealed record AnalysisRejected(NotAutomatableCategory Category, int Attempts, string Model) : RunEvent;

/// <summary>Se ha publicado (o adoptado tras una caída) el comentario <see cref="Kind"/> en la issue.</summary>
public sealed record IssueCommented(NotificationKind Kind, string CommentUrl) : RunEvent;

/// <summary>No se ha podido publicar el aviso <see cref="Kind"/> en la issue; el Estado de la Ejecución no cambia.</summary>
public sealed record NotificationFailed(NotificationKind Kind, string Error) : RunEvent;

/// <summary>Se ha creado y arrancado el Sandbox de la Ejecución con la imagen <see cref="Image"/> (AD-16 paso 2).</summary>
public sealed record SandboxCreated(string Image) : RunEvent;

/// <summary>Se ha liberado el Sandbox de la Ejecución: contenedor y volumen de trabajo borrados (AD-16 paso 8).</summary>
public sealed record SandboxReleased : RunEvent;

/// <summary>Consumo de tokens registrado por una llamada al Proveedor (AD-7, AD-15).</summary>
public sealed record TokensConsumed(string Role, string Provider, string Model, int InputTokens, int OutputTokens) : RunEvent;

/// <summary>Intento de salida desde el Sandbox rechazado por el Egress Gateway (FR-27, AD-7).</summary>
public sealed record EgressBlocked(string Target, string Reason) : RunEvent;

/// <summary>
/// Fragmento de la salida de un proceso del Sandbox (AD-3). El sumidero lo redacta y limita la salida acumulada a
/// <see cref="OutputTruncated.DefaultMaxBytes"/> bytes por Etapa.
/// </summary>
/// <param name="Stream"><c>stdout</c> o <c>stderr</c>.</param>
public sealed record OutputAppended(string Text, string Stream = "stdout") : RunEvent;

/// <summary>La salida de la Etapa ha superado <see cref="MaxBytes"/> bytes y el resto se descarta (se emite una sola vez).</summary>
public sealed record OutputTruncated(int MaxBytes = OutputTruncated.DefaultMaxBytes) : RunEvent
{
    /// <summary>5 MiB por Etapa.</summary>
    public const int DefaultMaxBytes = 5 * 1024 * 1024;
}

/// <summary>El intento <see cref="Attempt"/> del Programador ha fallado por <see cref="Reason"/> (FR-11).</summary>
public sealed record CodingAttemptFailed(int Attempt, string Reason) : RunEvent;

/// <summary>Un Revisor ha rechazado su salida número <see cref="Attempt"/> (esquema o reglas del Veredicto).</summary>
public sealed record ReviewerOutputRejected(int Round, ReviewerKind Reviewer, int Attempt, string Error) : RunEvent;

/// <summary>Veredicto persistido en <c>run_verdicts</c> para un Revisor en una Ronda (AD-11, FR-15).</summary>
public sealed record VerdictRecorded(int Round, ReviewerKind Reviewer, VerdictDecision Decision, int FindingCount, string Model) : RunEvent;

/// <summary>Clasificación determinista de riesgo emitida en la etapa <see cref="StageKind.Decision"/> (AD-9, FR-18).</summary>
public sealed record RiskClassified(RiskClass RiskClass, int DiffLines, IReadOnlyList<string> MatchedProtectedPaths) : RunEvent;

/// <summary>Pull Request abierto en GitHub y persistido en <c>gh_pull_requests</c> (FR-17, AD-8).</summary>
public sealed record PullRequestOpened(long Number, string Url, string Branch, string CommitSha) : RunEvent;

/// <summary>
/// Único punto de escritura de <c>run_events</c> (AD-3), implementado solo por Pipeline. Aplica
/// <see cref="ISecretRedactor"/> y asigna <see cref="RunEvent.Seq"/>; los productores no redactan.
/// Las transiciones de Estado (<see cref="RunCreated"/>, <see cref="StateChanged"/>) solo las emite
/// el agregado de Pipeline: aquí se rechazan.
/// </summary>
public interface IRunEventSink
{
    /// <summary>Persiste <paramref name="payload"/> y lo devuelve con <c>Seq</c>, <c>RunId</c> y <c>At</c>.</summary>
    /// <param name="stageId">Etapa que lo produce, si la hay (las Etapas llegan en historias posteriores).</param>
    Task<RunEvent> AppendAsync(Guid runId, Guid? stageId, RunEvent payload, CancellationToken cancellationToken);
}

/// <summary>Vista de lectura de una Ejecución.</summary>
public sealed record RunSummary(
    Guid RunId,
    string ShortId,
    RunState State,
    FailureReason? FailureReason,
    string? FailureDetail,
    Guid EventId,
    string ObjectKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Consultas de lectura de Ejecuciones (implementadas por Pipeline).</summary>
public interface IRunReader
{
    /// <summary>Busca por GUID o por <c>shortId</c> (8 caracteres hexadecimales). <c>null</c> si no existe.</summary>
    Task<RunSummary?> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>Eventos con <c>seq</c> &gt; <paramref name="afterSeq"/>, en orden de <c>seq</c>.</summary>
    Task<IReadOnlyList<RunEvent>> GetEventsAsync(Guid runId, long afterSeq, CancellationToken cancellationToken);

    /// <summary>La <see cref="Spec"/> de la Ejecución (<c>run_specs</c>), o <c>null</c> si todavía no hay.</summary>
    Task<Spec?> GetSpecAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>El último <see cref="TestReport"/> de la Ejecución (<c>run_test_reports</c>), o <c>null</c> si todavía no hay.</summary>
    Task<TestReport?> GetTestReportAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Veredictos persistidos de la Ejecución (<c>run_verdicts</c>), ordenados por ronda y revisor.</summary>
    Task<IReadOnlyList<RunVerdict>> GetVerdictsAsync(Guid runId, CancellationToken cancellationToken);
}
