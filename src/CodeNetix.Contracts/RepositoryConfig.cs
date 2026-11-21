using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace CodeNetix.Contracts;

/// <summary>Revisor de una Ejecución (FR-3). Se serializa en minúsculas, como en <c>codenetix.yml</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewerKind>))]
public enum ReviewerKind
{
    [JsonStringEnumMemberName("security")]
    Security,

    [JsonStringEnumMemberName("quality")]
    Quality,

    [JsonStringEnumMemberName("tests")]
    Tests,
}

/// <summary>Origen de la <see cref="EffectiveConfig"/>: <c>file</c> (hay <c>codenetix.yml</c>) o <c>defaults</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConfigSource>))]
public enum ConfigSource
{
    [JsonStringEnumMemberName("file")]
    File,

    [JsonStringEnumMemberName("defaults")]
    Defaults,
}

/// <summary>Modelos por rol que pide el repositorio (<c>models</c> en <c>codenetix.yml</c>); cada uno es opcional.</summary>
public sealed record AgentModels(string? Analyst, string? Coder, string? Reviewers);

/// <summary>
/// Configuración efectiva de una Ejecución (AD-10): instantánea inmutable que Pipeline calcula una sola vez a
/// partir de los límites de la instancia y de <c>codenetix.yml</c> (leído desde el <c>BaseSha</c>). El repositorio
/// solo puede restringir: los límites son el mínimo de ambos y las Rutas protegidas por defecto nunca se quitan.
/// </summary>
/// <param name="ProtectedPaths">Globs: los de por defecto más los del repositorio, sin repetir.</param>
/// <param name="Reviewers">Los del repositorio o, si no los define, los de por defecto.</param>
/// <param name="BudgetUsd"><c>null</c> = sin límite.</param>
/// <param name="Models">Los del repositorio, o <c>null</c>.</param>
public sealed record EffectiveConfig(
    ImmutableArray<string> ProtectedPaths,
    ImmutableArray<ReviewerKind> Reviewers,
    int MaxReviewRounds,
    int MaxCodingAttempts,
    decimal? BudgetUsd,
    AgentModels? Models,
    ConfigSource Source)
{
    /// <summary>Revisores por defecto (FR-3).</summary>
    public static readonly ImmutableArray<ReviewerKind> DefaultReviewers =
        [ReviewerKind.Security, ReviewerKind.Quality, ReviewerKind.Tests];

    /// <summary>Rutas protegidas por defecto (FR-3): siempre forman parte de la configuración efectiva.</summary>
    public static readonly ImmutableArray<string> DefaultProtectedPaths =
        ["**/Migrations/**", "**/*.csproj", ".github/**"];
}

/// <summary>Repositorio clonado y fijado en su commit base (AD-16 paso 1).</summary>
/// <param name="WorkspacePath">Árbol de trabajo (<c>&lt;DataDirectory&gt;/workspaces/&lt;shortId&gt;</c>).</param>
/// <param name="BaseSha"><c>git rev-parse HEAD</c> tras el clon.</param>
/// <param name="RepoConfigYaml">Contenido de <c>codenetix.yml</c> en <see cref="BaseSha"/>, o <c>null</c> si no existe.</param>
public sealed record PreparedRepository(string WorkspacePath, string BaseSha, string? RepoConfigYaml);

/// <summary>
/// Árbol de trabajo de una Ejecución (AD-8: solo lo implementa el módulo GitHub). Clona con credenciales
/// efímeras (nunca quedan en el árbol), fija el <c>BaseSha</c> y lee <c>codenetix.yml</c> desde ese commit.
/// </summary>
public interface IRepositoryWorkspace
{
    /// <summary>
    /// Clona la rama por defecto de <paramref name="owner"/>/<paramref name="repo"/>. Si algo falla (clon,
    /// comprobación de credenciales, lectura), borra el árbol y lanza <see cref="RepositoryPreparationException"/>.
    /// </summary>
    Task<PreparedRepository> PrepareAsync(Guid runId, string owner, string repo, CancellationToken cancellationToken);

    /// <summary>
    /// Rutas versionadas del árbol de trabajo de <paramref name="runId"/> (<c>git ls-files</c>), con <c>/</c>.
    /// Lanza <see cref="RepositoryPreparationException"/> si el árbol no existe o git falla.
    /// </summary>
    Task<IReadOnlyList<string>> ListFilesAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Escribe en <paramref name="destination"/> el tar del árbol del <c>BaseSha</c> de <paramref name="runId"/>
    /// (<c>git archive --format=tar</c>): solo los archivos versionados y nunca <c>.git</c>. Lanza
    /// <see cref="RepositoryPreparationException"/> si el árbol no existe o git falla.
    /// </summary>
    Task ExportTreeAsync(Guid runId, Stream destination, CancellationToken cancellationToken);

    /// <summary>
    /// Sincroniza el árbol exportado del Sandbox (<paramref name="workspaceTar"/>) en el clon del host de
    /// <paramref name="runId"/> (excluyendo <c>.git</c>, <c>bin/</c> y <c>obj/</c>) y calcula el diff unificado contra
    /// <paramref name="baseSha"/> incluyendo archivos nuevos (AD-8). Lanza <see cref="RepositoryPreparationException"/>
    /// si el árbol no existe, el tar es inválido o git falla.
    /// </summary>
    Task<DiffResult> ComputeDiffAsync(Guid runId, Stream workspaceTar, string baseSha, CancellationToken cancellationToken);

    /// <summary>Borra el árbol de trabajo de <paramref name="runId"/> (no falla si no existe).</summary>
    Task DeleteAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>No se ha podido preparar el repositorio; <see cref="Exception.Message"/> es el motivo (sin el token).</summary>
public sealed class RepositoryPreparationException : Exception
{
    public RepositoryPreparationException()
        : base("no se ha podido clonar el repositorio")
    {
    }

    public RepositoryPreparationException(string reason)
        : base(reason)
    {
    }

    public RepositoryPreparationException(string reason, Exception innerException)
        : base(reason, innerException)
    {
    }
}

/// <summary>Clave de objeto de GitHub (<see cref="EventEnvelope.ObjectKey"/>): <c>github:&lt;owner&gt;/&lt;repo&gt;#&lt;n&gt;</c>.</summary>
public static class GitHubObjectKey
{
    public const string Prefix = "github:";

    public static string Format(string owner, string repo, long number) => $"{Prefix}{owner}/{repo}#{number}";

    /// <summary><c>false</c> si <paramref name="objectKey"/> no es una issue de GitHub.</summary>
    public static bool TryParse(string? objectKey, out string owner, out string repo, out long number)
    {
        owner = string.Empty;
        repo = string.Empty;
        number = 0;
        if (objectKey is null || !objectKey.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = objectKey.AsSpan(Prefix.Length);
        var slash = rest.IndexOf('/');
        var hash = rest.LastIndexOf('#');
        if (slash <= 0 || hash <= slash + 1 || hash == rest.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(rest[(hash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        var ownerSpan = rest[..slash];
        var repoSpan = rest[(slash + 1)..hash];
        if (repoSpan.Contains('/') || !IsName(ownerSpan) || !IsName(repoSpan))
        {
            return false;
        }

        number = parsed;
        owner = ownerSpan.ToString();
        repo = repoSpan.ToString();
        return true;
    }

    /// <summary>
    /// Letras, dígitos, <c>-</c>, <c>_</c> y <c>.</c>, sin empezar por <c>-</c> (no parece una opción) ni ser
    /// <c>.</c> o <c>..</c>: nada que escape de la URL o de una ruta.
    /// </summary>
    public static bool IsName(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value is "." or ".." || value[0] == '-')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary><c>runId</c> corto (8 caracteres) solo para mostrar o nombrar.</summary>
public static class RunShortId
{
    /// <summary>
    /// Los 8 últimos caracteres hexadecimales del id. En un GUID v7 los 8 primeros son la marca de tiempo
    /// (iguales para todas las Ejecuciones creadas en el mismo minuto); los últimos son aleatorios.
    /// </summary>
    public static string Of(Guid runId) => runId.ToString("N")[^8..];
}
