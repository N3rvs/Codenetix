using CodeNetix.Contracts;
using Microsoft.Extensions.Logging;

namespace CodeNetix.GitHub;

/// <summary>
/// Guardián Git único del módulo GitHub (AD-8): toda operación de push o merge pasa por aquí.
/// Rechaza y registra en el log cualquier intento de push a la rama por defecto, a cualquier rama sin prefijo
/// <c>codenetix/</c>, o cualquier intento de merge (git o API).
/// </summary>
internal sealed partial class GitSafetyGuard(ILogger<GitSafetyGuard> logger)
{
    public const string AllowedBranchPrefix = PullRequestBranch.Prefix;

    private const string HeadsRefPrefix = "refs/heads/";

    /// <summary>
    /// Valida que <paramref name="targetBranch"/> no sea la rama por defecto (<paramref name="defaultBranch"/>)
    /// y tenga el prefijo obligatorio <c>codenetix/</c>. Si no cumple, registra en el log y lanza
    /// <see cref="GitOperationBlockedException"/>.
    /// </summary>
    public void EnsurePushAllowed(string targetBranch, string defaultBranch)
    {
        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            const string Reason = "Operación git bloqueada: la rama destino del push está vacía.";
            LogPushBlocked("<vacía>", defaultBranch ?? "<desconocida>", Reason);
            throw new GitOperationBlockedException(Reason);
        }

        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            const string Reason = "Operación git bloqueada: no se ha determinado la rama por defecto del repositorio.";
            LogPushBlocked(targetBranch, "<desconocida>", Reason);
            throw new GitOperationBlockedException(Reason);
        }

        var normalizedTarget = StripHeadsPrefix(targetBranch.Trim());
        var normalizedDefault = StripHeadsPrefix(defaultBranch.Trim());

        if (string.Equals(normalizedTarget, normalizedDefault, StringComparison.OrdinalIgnoreCase))
        {
            var reason = $"Operación git bloqueada: prohibido hacer push a la rama por defecto '{normalizedDefault}'.";
            LogPushBlocked(targetBranch, normalizedDefault, reason);
            throw new GitOperationBlockedException(reason);
        }

        if (!normalizedTarget.StartsWith(AllowedBranchPrefix, StringComparison.Ordinal)
            || normalizedTarget.Length == AllowedBranchPrefix.Length
            || !IsSafeBranchName(normalizedTarget))
        {
            var reason = $"Operación git bloqueada: la rama '{targetBranch}' no tiene el prefijo obligatorio '{AllowedBranchPrefix}' o no es un nombre de rama válido.";
            LogPushBlocked(targetBranch, normalizedDefault, reason);
            throw new GitOperationBlockedException(reason);
        }
    }

    /// <summary>
    /// Rechaza incondicionalmente cualquier intento de merge (git o API), registrándolo en el log y lanzando
    /// <see cref="GitOperationBlockedException"/> (AD-8).
    /// </summary>
    public void EnsureMergeForbidden(string operation, string? target = null)
    {
        var op = string.IsNullOrWhiteSpace(operation) ? "merge" : operation.Trim();
        var tgt = string.IsNullOrWhiteSpace(target) ? "<sin destino>" : target.Trim();
        var reason = $"Operación git bloqueada: el merge automático está prohibido ({op} sobre '{tgt}').";
        LogMergeBlocked(op, tgt);
        throw new GitOperationBlockedException(reason);
    }

    private static string StripHeadsPrefix(string branch) =>
        branch.StartsWith(HeadsRefPrefix, StringComparison.Ordinal)
            ? branch[HeadsRefPrefix.Length..]
            : branch;

    private static bool IsSafeBranchName(string branch)
    {
        if (branch[0] == '-'
            || branch.EndsWith('/')
            || branch.EndsWith('.')
            || branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || branch.Contains("..", StringComparison.Ordinal)
            || branch.Contains(":", StringComparison.Ordinal)
            || branch.Contains("@{", StringComparison.Ordinal)
            || branch.Contains("//", StringComparison.Ordinal)
            || branch.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in branch.Split('/'))
        {
            if (segment.Length == 0
                || segment.StartsWith('.')
                || segment.EndsWith('.')
                || segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (var c in branch)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '~' or '^' or '?' or '*' or '[')
            {
                return false;
            }
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "GitSafetyGuard rechazó push a '{TargetBranch}' (rama por defecto '{DefaultBranch}'): {Reason}")]
    private partial void LogPushBlocked(string targetBranch, string defaultBranch, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "GitSafetyGuard rechazó intento de merge ({Operation}) sobre '{Target}'.")]
    private partial void LogMergeBlocked(string operation, string target);
}
