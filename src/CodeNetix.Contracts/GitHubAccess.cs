namespace CodeNetix.Contracts;

/// <summary>
/// Identidad del token de GitHub de la instancia (AD-8: solo la implementa el módulo GitHub).
/// </summary>
public interface IGitHubIdentity
{
    /// <summary>
    /// Login del token de la instancia (<c>GET /user</c>), cacheado tras la primera llamada con éxito.
    /// Lanza <see cref="GitHubUnavailableException"/> si GitHub no responde o el token no es válido.
    /// </summary>
    Task<string> GetLoginAsync(CancellationToken cancellationToken);
}

/// <summary>Permisos sobre un Repositorio (AD-8: solo la implementa el módulo GitHub).</summary>
public interface IRepositoryPermissions
{
    /// <summary>
    /// ¿Tiene <paramref name="login"/> permiso de escritura en <paramref name="owner"/>/<paramref name="repo"/>?
    /// Nunca lanza por errores de GitHub: los devuelve como <see cref="WriteAccessStatus.Unavailable"/>.
    /// </summary>
    Task<WriteAccess> GetWriteAccessAsync(string owner, string repo, string login, CancellationToken cancellationToken);
}

public enum WriteAccessStatus
{
    /// <summary><c>admin</c>, <c>maintain</c> o <c>write</c>.</summary>
    Granted,

    /// <summary><c>read</c>, <c>triage</c>, <c>none</c>, o repositorio invisible / no colaborador (404).</summary>
    Denied,

    /// <summary>No se ha podido saber (token inválido, red, 5xx). Se falla cerrado.</summary>
    Unavailable,
}

/// <summary>Resultado de <see cref="IRepositoryPermissions.GetWriteAccessAsync"/>; <see cref="Reason"/> solo en <c>Unavailable</c>.</summary>
public sealed record WriteAccess(WriteAccessStatus Status, string? Reason)
{
    public static readonly WriteAccess Granted = new(WriteAccessStatus.Granted, null);

    public static readonly WriteAccess Denied = new(WriteAccessStatus.Denied, null);

    /// <summary>Solo en <c>Unavailable</c>: GitHub limitó las peticiones (rate limit); reintentar enseguida no sirve.</summary>
    public bool RateLimited { get; init; }

    public static WriteAccess Unavailable(string reason) => new(WriteAccessStatus.Unavailable, reason);
}

/// <summary>GitHub no ha podido responder a una consulta; <see cref="Exception.Message"/> es el motivo (sin secretos).</summary>
public sealed class GitHubUnavailableException : Exception
{
    public GitHubUnavailableException()
        : base("GitHub no responde")
    {
    }

    public GitHubUnavailableException(string reason)
        : base(reason)
    {
    }

    public GitHubUnavailableException(string reason, Exception innerException)
        : base(reason, innerException)
    {
    }
}
