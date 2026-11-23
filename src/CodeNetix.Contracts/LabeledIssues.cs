using System.Globalization;

namespace CodeNetix.Contracts;

/// <summary>
/// Clave de deduplicación de un Evento de GitHub disparado por una etiqueta (AD-4), compartida por el sondeo (7.1) y el
/// webhook (7.2): <c>gh:&lt;owner&gt;/&lt;repo&gt;#&lt;issue&gt;:labeled:&lt;labelEventId&gt;</c>, con owner y repo en minúsculas.
/// </summary>
public static class GitHubLabelEventKey
{
    public const string Prefix = "gh:";

    public static string Format(string owner, string repo, long issueNumber, long labelEventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}#{issueNumber}:labeled:{labelEventId}");
    }
}

/// <summary>
/// Fallo de una consulta a GitHub del sondeo: <see cref="Reason"/> es un motivo fijo y sin secretos;
/// <see cref="RetryAfter"/>, cuánto esperar si GitHub lo indica (rate limit).
/// </summary>
public sealed record GitHubQueryFailure(string Reason, bool RateLimited, TimeSpan? RetryAfter);

/// <summary>Issue abierta con la etiqueta; <see cref="UpdatedAt"/> es el <c>updated_at</c> de GitHub tal cual (para detectar cambios).</summary>
public sealed record LabeledIssue(long Number, string UpdatedAt);

public enum LabeledIssueListStatus
{
    /// <summary><see cref="LabeledIssueList.Issues"/> tiene la lista (sin pull requests).</summary>
    Ok,

    /// <summary>GitHub respondió <c>304</c> al <c>ETag</c> enviado: nada ha cambiado.</summary>
    NotModified,

    /// <summary>No se ha podido consultar: <see cref="LabeledIssueList.Failure"/> dice por qué.</summary>
    Unavailable,
}

/// <param name="ETag"><c>ETag</c> de la primera página, para la siguiente consulta condicional.</param>
/// <param name="Truncated">Había más páginas que el tope: la lista está incompleta.</param>
public sealed record LabeledIssueList(
    LabeledIssueListStatus Status,
    IReadOnlyList<LabeledIssue> Issues,
    string? ETag,
    bool Truncated,
    GitHubQueryFailure? Failure)
{
    public static LabeledIssueList NotModified(string? etag) => new(LabeledIssueListStatus.NotModified, [], etag, false, null);

    public static LabeledIssueList Unavailable(GitHubQueryFailure failure) => new(LabeledIssueListStatus.Unavailable, [], null, false, failure);
}

/// <summary>Evento <c>labeled</c> de una issue: <see cref="Actor"/> es quien puso la etiqueta (<c>null</c> si GitHub no lo da).</summary>
public sealed record LabelEvent(long Id, string? Actor, DateTimeOffset? CreatedAt);

public enum LabelEventLookupStatus
{
    Found,

    /// <summary>No hay un evento <c>labeled</c> con esa etiqueta en las páginas revisadas (o la issue ya no existe).</summary>
    NotFound,

    Unavailable,
}

public sealed record LabelEventLookup(LabelEventLookupStatus Status, LabelEvent? Event, GitHubQueryFailure? Failure)
{
    public static readonly LabelEventLookup NotFound = new(LabelEventLookupStatus.NotFound, null, null);

    /// <summary><c>NotFound</c> porque GitHub no ve la issue (404/410: borrada, transferida o repositorio invisible para el token).</summary>
    public static readonly LabelEventLookup IssueNotFound = new(LabelEventLookupStatus.NotFound, null, null) { IssueMissing = true };

    /// <summary>Solo con <c>NotFound</c>: la issue (o el repositorio) no existe para el token; volver a preguntar no cambia nada.</summary>
    public bool IssueMissing { get; init; }

    public static LabelEventLookup Found(LabelEvent labelEvent) => new(LabelEventLookupStatus.Found, labelEvent, null);

    public static LabelEventLookup Unavailable(GitHubQueryFailure failure) => new(LabelEventLookupStatus.Unavailable, null, failure);
}

/// <summary>
/// Issues etiquetadas de un Repositorio (AD-8: solo lo implementa el módulo GitHub). Nunca lanza por errores de GitHub
/// (red, 5xx, token, rate limit): los devuelve como <c>Unavailable</c>.
/// </summary>
public interface ILabeledIssueSource
{
    /// <summary>
    /// Issues abiertas con <paramref name="label"/> (sin pull requests), las actualizadas más recientemente primero. Con
    /// <paramref name="etag"/>, la primera página es condicional y un <c>304</c> devuelve <see cref="LabeledIssueListStatus.NotModified"/>.
    /// </summary>
    Task<LabeledIssueList> ListOpenAsync(string owner, string repo, string label, string? etag, CancellationToken cancellationToken);

    /// <summary>El evento <c>labeled</c> más reciente de <paramref name="label"/> (sin distinguir mayúsculas) en la issue.</summary>
    Task<LabelEventLookup> FindLatestLabelEventAsync(string owner, string repo, long issueNumber, string label, CancellationToken cancellationToken);
}
