namespace CodeNetix.Contracts;

/// <summary>Fuente de un Evento (AD-4).</summary>
public enum EventSource
{
    Manual,
    GitHub,
    Sentry,
    GitHubAction,
}

/// <summary>Estado de un Evento tras pasar por Ingestion.</summary>
public enum EventStatus
{
    Accepted,
    Ignored,
}

/// <summary>
/// Evento normalizado que produce toda Fuente (AD-4). <see cref="Payload"/> es JSON tratado siempre
/// como dato (nunca contiene el token ni otros secretos). Ids con <c>Guid.CreateVersion7()</c>; fechas UTC.
/// </summary>
/// <param name="ObjectKey">Objeto sobre el que se trabaja, p. ej. <c>github:owner/repo#12</c>.</param>
/// <param name="DedupKey">Clave única de deduplicación, p. ej. <c>manual:&lt;url&gt;:&lt;requestId&gt;</c>.</param>
/// <param name="Actor">Quién lo disparó (login de GitHub). Para el Evento manual, la identidad del token de la instancia.</param>
/// <param name="Payload">JSON con los datos del Evento; su texto es siempre dato no confiable (<see cref="UntrustedText"/>).</param>
public sealed record EventEnvelope(
    Guid Id,
    EventSource Source,
    string ObjectKey,
    string DedupKey,
    string? Actor,
    string Payload,
    EventStatus Status,
    string? IgnoredReason,
    DateTimeOffset ReceivedAt);

/// <summary>Resultado de <see cref="IManualEventIngestion.SubmitAsync"/>.</summary>
public enum ManualEventOutcome
{
    /// <summary>Se ha creado un Evento nuevo (<c>Accepted</c> o <c>Ignored</c>).</summary>
    Created,

    /// <summary>Ya existía un Evento con la misma clave (mismo <c>requestId</c>): no se duplica.</summary>
    Existing,

    /// <summary>La URL no es una issue de GitHub admitida. No se persiste nada.</summary>
    Rejected,

    /// <summary>
    /// La petición es válida pero la Configuración de instancia no permite crear el Evento (Sentry sin configurar, otra
    /// organización, incidencia inexistente o token de Sentry rechazado, proyecto sin Repositorio objetivo, GitHub rechaza
    /// la issue de seguimiento). No se persiste nada.
    /// </summary>
    Unprocessable,

    /// <summary>No se ha podido autorizar el disparador (GitHub no responde o token inválido). No se persiste nada.</summary>
    Unavailable,

    /// <summary>Sentry no responde (red, 5xx, timeout, rate limit). No se persiste nada; se puede reintentar.</summary>
    SentryUnavailable,
}

/// <summary>Evento persistido tal como lo ve quien lo envió.</summary>
/// <param name="Repository"><c>owner/repo</c> del Repositorio objetivo.</param>
/// <param name="IgnoredReason">Solo en <see cref="EventStatus.Ignored"/>, p. ej. <c>"no autorizado"</c>.</param>
public sealed record ManualEventInfo(Guid Id, EventStatus Status, string? Actor, string Repository, string? IgnoredReason);

/// <summary>
/// Resultado de enviar un Evento manual: <see cref="Event"/> tiene valor en <c>Created</c> y
/// <c>Existing</c>; <see cref="Reason"/>, en <c>Rejected</c>, <c>Unprocessable</c>, <c>Unavailable</c> y <c>SentryUnavailable</c>.
/// </summary>
public sealed record ManualEventResult(ManualEventOutcome Outcome, ManualEventInfo? Event, string? Reason)
{
    public Guid? EventId => Event?.Id;

    public static ManualEventResult Created(ManualEventInfo info) => new(ManualEventOutcome.Created, info, null);

    public static ManualEventResult Existing(ManualEventInfo info) => new(ManualEventOutcome.Existing, info, null);

    public static ManualEventResult Rejected(string reason) => new(ManualEventOutcome.Rejected, null, reason);

    public static ManualEventResult Unprocessable(string reason) => new(ManualEventOutcome.Unprocessable, null, reason);

    public static ManualEventResult Unavailable(string reason) => new(ManualEventOutcome.Unavailable, null, reason);

    public static ManualEventResult SentryUnavailable(string reason) => new(ManualEventOutcome.SentryUnavailable, null, reason);
}

/// <summary>Entrada de trabajo manual (<c>codenetix fix</c>, <c>POST /api/events/manual</c>).</summary>
public interface IManualEventIngestion
{
    /// <summary>
    /// Normaliza <paramref name="url"/> a un <see cref="EventEnvelope"/> con <see cref="EventSource.Manual"/>
    /// y lo persiste con la clave <c>manual:&lt;url-canónica&gt;:&lt;requestId&gt;</c>. Idempotente por
    /// <paramref name="requestId"/>. Antes de persistir autoriza al actor (escritura en el Repositorio);
    /// sin permiso, el Evento queda <see cref="EventStatus.Ignored"/>; si GitHub no responde, no se persiste nada.
    /// </summary>
    Task<ManualEventResult> SubmitAsync(string url, Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// Lo que Host lee del runner de GitHub Actions para <c>codenetix action</c> (AD-12). Solo Host lee el entorno.
/// </summary>
/// <param name="EventName"><c>GITHUB_EVENT_NAME</c>.</param>
/// <param name="EventPayloadJson">Contenido de <c>GITHUB_EVENT_PATH</c> (dato no confiable).</param>
/// <param name="Repository"><c>GITHUB_REPOSITORY</c> (<c>owner/repo</c>), si está definido.</param>
/// <param name="WorkflowRunId"><c>GITHUB_RUN_ID</c>, si está definido.</param>
/// <param name="WorkflowRunAttempt"><c>GITHUB_RUN_ATTEMPT</c>, si está definido.</param>
/// <param name="TriggerLabel">Etiqueta que dispara el trabajo (por defecto <c>codenetix</c>).</param>
public sealed record GitHubActionEventInput(
    string EventName,
    string EventPayloadJson,
    string? Repository,
    string? WorkflowRunId,
    string? WorkflowRunAttempt,
    string TriggerLabel);

/// <summary>Disparo válido de <c>codenetix action</c>: una issue abierta etiquetada con la etiqueta de disparo.</summary>
/// <param name="Owner">Owner en minúsculas.</param>
/// <param name="Repo">Repositorio en minúsculas.</param>
/// <param name="Sender">Login de quien puso la etiqueta: el actor que se autoriza (FR-26).</param>
public sealed record GitHubActionTrigger(
    string Owner,
    string Repo,
    long IssueNumber,
    string Sender,
    string Label,
    string ObjectKey,
    string DedupKey,
    string? WorkflowRunId,
    string? WorkflowRunAttempt);

public enum GitHubActionEventKind
{
    /// <summary>Issue etiquetada con la etiqueta de disparo: <see cref="GitHubActionEventParse.Trigger"/> tiene valor.</summary>
    Applicable,

    /// <summary>Evento legítimo que no dispara trabajo (otro evento, otra acción u otra etiqueta, issue cerrada…).</summary>
    NotApplicable,

    /// <summary>Evento ilegible o incoherente con el entorno del runner.</summary>
    Invalid,
}

/// <summary>Resultado de <see cref="IGitHubActionEventIngestion.Parse"/>; <see cref="Reason"/> fuera de <c>Applicable</c>.</summary>
public sealed record GitHubActionEventParse(GitHubActionEventKind Kind, GitHubActionTrigger? Trigger, string? Reason)
{
    public static GitHubActionEventParse Applicable(GitHubActionTrigger trigger) => new(GitHubActionEventKind.Applicable, trigger, null);

    public static GitHubActionEventParse NotApplicable(string reason) => new(GitHubActionEventKind.NotApplicable, null, reason);

    public static GitHubActionEventParse Invalid(string reason) => new(GitHubActionEventKind.Invalid, null, reason);
}

/// <summary>
/// Entrada de trabajo de <c>codenetix action</c> (AD-4, AD-12): Fuente <see cref="EventSource.GitHubAction"/>, sin firma
/// pero con la autorización de FR-26.
/// </summary>
public interface IGitHubActionEventIngestion
{
    /// <summary>Normaliza el evento del runner sin E/S. Solo <c>issues</c>/<c>labeled</c> con la etiqueta de disparo aplica.</summary>
    GitHubActionEventParse Parse(GitHubActionEventInput input);

    /// <summary>
    /// Autoriza a <see cref="GitHubActionTrigger.Sender"/> (escritura en el Repositorio) y persiste el Evento con la clave
    /// <see cref="GitHubActionTrigger.DedupKey"/> (idempotente). Sin permiso, queda <see cref="EventStatus.Ignored"/>; si
    /// GitHub no responde, no se persiste nada (<see cref="ManualEventOutcome.Unavailable"/>). Devuelve
    /// <c>Created</c>, <c>Existing</c> o <c>Unavailable</c>, con la misma forma que el Evento manual.
    /// </summary>
    Task<ManualEventResult> SubmitAsync(GitHubActionTrigger trigger, CancellationToken cancellationToken);
}
