using System.Text.Json.Serialization;

namespace CodeNetix.Contracts;

/// <summary>Tipo de aviso que CodeNetix publica en una issue (AD-8). Es parte de la clave de idempotencia.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotificationKind>))]
public enum NotificationKind
{
    /// <summary>Rechazo temprano (FR-10): la issue no es automatizable.</summary>
    NotAutomatable,

    /// <summary>Cierre con éxito (FR-19): se ha abierto un Pull Request para la issue.</summary>
    PullRequestCreated,

    /// <summary>Cierre en error (FR-19): la Ejecución ha terminado en <see cref="RunState.Failed"/> (motivo distinto de <see cref="FailureReason.NotAutomatable"/>).</summary>
    RunFailed,

    /// <summary>Cierre por cancelación (FR-19): la Ejecución ha terminado en <see cref="RunState.Cancelled"/>.</summary>
    RunCancelled,
}

/// <summary>Estado de un aviso en <c>gh_notifications</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotificationStatus>))]
public enum NotificationStatus
{
    /// <summary>Reservado; todavía no se sabe si el comentario existe.</summary>
    Pending,

    /// <summary>El comentario existe (publicado o adoptado).</summary>
    Posted,

    /// <summary>GitHub ha rechazado la publicación; no se reintenta en segundo plano.</summary>
    Failed,
}

/// <summary>Resultado de <see cref="IIssueNotifier.NotifyAsync"/>.</summary>
/// <param name="CommentUrl">URL del comentario (solo en <see cref="NotificationStatus.Posted"/>).</param>
/// <param name="Error">Motivo legible y redactado (solo en <see cref="NotificationStatus.Failed"/>).</param>
public sealed record IssueNotification(NotificationStatus Status, string? CommentUrl, string? Error)
{
    public static IssueNotification Posted(string commentUrl) => new(NotificationStatus.Posted, commentUrl, null);

    public static IssueNotification Failed(string error) => new(NotificationStatus.Failed, null, error);
}

/// <summary>
/// Avisos en issues (AD-8: solo los implementa el módulo GitHub). Idempotente por (<c>runId</c>, <c>kind</c>): como
/// mucho un comentario por clave, aunque se llame varias veces, en paralelo o tras una caída.
/// </summary>
public interface IIssueNotifier
{
    /// <summary>
    /// Publica <paramref name="body"/> (Markdown; se trata como no confiable y se sanea) como comentario de la issue,
    /// con un marcador oculto <c>&lt;!-- codenetix:run=&lt;runId&gt;;kind=&lt;kind&gt; --&gt;</c>. Los errores de GitHub se
    /// devuelven como <see cref="NotificationStatus.Failed"/>; no lanza salvo por cancelación o fallos de la BD.
    /// </summary>
    Task<IssueNotification> NotifyAsync(
        Guid runId,
        string owner,
        string repo,
        long number,
        NotificationKind kind,
        string body,
        CancellationToken cancellationToken);
}
