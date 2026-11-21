using System.Globalization;

namespace CodeNetix.Contracts;

/// <summary>Convención de nombres de rama de CodeNetix (AD-8): siempre <c>codenetix/&lt;runId&gt;</c>.</summary>
public static class PullRequestBranch
{
    public const string Prefix = "codenetix/";

    public static string For(Guid runId) => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{runId:D}");
}

/// <summary>Solicitud para crear commit, push a <c>codenetix/&lt;runId&gt;</c> y abrir un Pull Request en GitHub (AD-8, FR-17).</summary>
public sealed record PullRequestRequest(
    Guid RunId,
    string Owner,
    string Repo,
    string BaseSha,
    string Title,
    string Body,
    string CommitMessage);

/// <summary>Pull Request publicado y persistido en <c>gh_pull_requests</c> (AD-11, FR-17).</summary>
public sealed record PullRequestInfo(
    long Number,
    string Url,
    string BranchName,
    string CommitSha,
    string Title,
    DateTimeOffset CreatedAt);

/// <summary>
/// Publicador de Pull Requests (AD-1, AD-8: solo lo implementa <c>CodeNetix.GitHub</c>).
/// Idempotente por <c>runId</c>: si ya existe en <c>gh_pull_requests</c>, devuelve el existente sin duplicar push ni PR.
/// </summary>
public interface IPullRequestPublisher
{
    /// <summary>
    /// Crea un único commit sobre <see cref="PullRequestRequest.BaseSha"/> en la rama <c>codenetix/&lt;runId&gt;</c>,
    /// hace push validado por <c>GitSafetyGuard</c>, abre el PR contra la rama por defecto, intenta añadir la etiqueta
    /// <c>codenetix</c> y persiste el resultado en <c>gh_pull_requests</c>.
    /// </summary>
    Task<PullRequestInfo> PublishAsync(PullRequestRequest request, CancellationToken cancellationToken);

    /// <summary>Devuelve el PR publicado para <paramref name="runId"/>, o <c>null</c> si todavía no existe.</summary>
    Task<PullRequestInfo?> FindByRunAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>
/// Operación git prohibida bloqueada por <c>GitSafetyGuard</c> (AD-8): push a rama por defecto, rama fuera de
/// <c>codenetix/*</c> o cualquier intento de merge.
/// </summary>
public sealed class GitOperationBlockedException : Exception
{
    public GitOperationBlockedException()
        : base("Operación git bloqueada por GitSafetyGuard.")
    {
    }

    public GitOperationBlockedException(string message)
        : base(message)
    {
    }

    public GitOperationBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>No se ha podido publicar el Pull Request en GitHub; <see cref="Exception.Message"/> está redactado.</summary>
public sealed class PullRequestPublishException : Exception
{
    public PullRequestPublishException()
        : base("No se pudo publicar el Pull Request en GitHub.")
    {
    }

    public PullRequestPublishException(string message)
        : base(message)
    {
    }

    public PullRequestPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
