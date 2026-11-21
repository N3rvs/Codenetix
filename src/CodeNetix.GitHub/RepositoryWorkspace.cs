using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using CodeNetix.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeNetix.GitHub;

/// <summary>Resultado de <see cref="RepositoryWorkspace.CommitAndPushBranchAsync"/>.</summary>
internal sealed record PushedBranch(string BranchName, string DefaultBranch, string CommitSha);

/// <summary>
/// Árbol de trabajo de una Ejecución (AD-8, AD-16 paso 1) en <c>&lt;WorkspacesDirectory&gt;/&lt;shortId&gt;</c>:
/// <list type="number">
/// <item><c>git clone --depth 1 --no-tags</c> de la rama por defecto. El token viaja solo en variables de entorno
/// del proceso <c>git</c> (<c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_n</c>/<c>GIT_CONFIG_VALUE_n</c> con
/// <c>http.extraHeader</c>): nunca en los argumentos, en la URL ni en <c>.git/config</c>.</item>
/// <item><c>BaseSha</c> = <c>git rev-parse HEAD</c>.</item>
/// <item>Comprobación: <c>git config --list --local</c> sin <c>extraheader</c> ni el token, y la URL de
/// <c>origin</c> sin userinfo.</item>
/// <item><c>codenetix.yml</c> con <c>git show &lt;BaseSha&gt;:codenetix.yml</c> (nunca del árbol de trabajo).</item>
/// </list>
/// Si algo falla se borra el árbol y se lanza <see cref="RepositoryPreparationException"/> con un motivo sin el token.
/// </summary>
internal sealed partial class RepositoryWorkspace : IRepositoryWorkspace
{
    private readonly IOptions<GitHubOptions> options;
    private readonly ILogger<RepositoryWorkspace> logger;
    private readonly GitSafetyGuard _safetyGuard;
    private readonly ISecretRedactor? _redactor;

    public RepositoryWorkspace(IOptions<GitHubOptions> options, ILogger<RepositoryWorkspace> logger)
        : this(options, logger, new GitSafetyGuard(NullLogger<GitSafetyGuard>.Instance), redactor: null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public RepositoryWorkspace(
        IOptions<GitHubOptions> options,
        ILogger<RepositoryWorkspace> logger,
        GitSafetyGuard safetyGuard,
        ISecretRedactor? redactor = null)
    {
        this.options = options;
        this.logger = logger;
        _safetyGuard = safetyGuard ?? throw new ArgumentNullException(nameof(safetyGuard));
        _redactor = redactor;
    }

    public const string RepoConfigFileName = "codenetix.yml";

    public static readonly TimeSpan DefaultCloneTimeout = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Timeout de <c>git archive</c> (<see cref="ExportTreeAsync"/>).</summary>
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(120);

    private const int MaxReasonLength = 300;

    /// <summary>Timeout del clon (120 s; las pruebas pueden acortarlo).</summary>
    internal TimeSpan CloneTimeout { get; init; } = DefaultCloneTimeout;

    public async Task<PreparedRepository> PrepareAsync(Guid runId, string owner, string repo, CancellationToken cancellationToken)
    {
        var path = WorkspacePathOf(runId);
        var cloneUrl = CloneUrlOf(owner, repo);

        // Un árbol de un intento anterior (proceso caído a medias) se descarta: se clona siempre desde cero.
        DeleteTree(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var completed = false;
        try
        {
            var prepared = await CloneAndReadAsync(path, cloneUrl, owner, repo, cancellationToken).ConfigureAwait(false);
            completed = true;
            LogPrepared(owner, repo, prepared.BaseSha, RunShortId.Of(runId));
            return prepared;
        }
        finally
        {
            if (!completed)
            {
                DeleteTree(path);
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(Guid runId, CancellationToken cancellationToken)
    {
        var path = WorkspacePathOf(runId);
        if (!Directory.Exists(path))
        {
            throw new RepositoryPreparationException("el árbol de trabajo de la Ejecución no existe");
        }

        // -z: rutas separadas por NUL y sin comillas ni escapes (nombres con espacios o no ASCII tal cual).
        var output = await RunCheckedAsync(path, ["ls-files", "-z"], token: null, cancellationToken).ConfigureAwait(false);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public async Task ExportTreeAsync(Guid runId, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var path = WorkspacePathOf(runId);
        if (!Directory.Exists(path))
        {
            throw new RepositoryPreparationException("el árbol de trabajo de la Ejecución no existe");
        }

        // El clon no se modifica tras fijar el BaseSha: HEAD es el BaseSha. Se exporta por SHA, no por nombre de ref.
        var baseSha = (await RunCheckedAsync(path, ["rev-parse", "--verify", "HEAD"], token: null, cancellationToken).ConfigureAwait(false)).Trim();
        if (!IsSha(baseSha))
        {
            throw new RepositoryPreparationException("no se ha podido resolver el commit base del árbol de trabajo");
        }

        // $GIT_DIR/info/attributes tiene prioridad sobre los .gitattributes del repositorio: así export-ignore no puede
        // quitar archivos ni export-subst reescribirlos (el Sandbox ve exactamente el árbol del BaseSha).
        var info = Path.Combine(path, ".git", "info");
        Directory.CreateDirectory(info);
        await File.WriteAllTextAsync(Path.Combine(info, "attributes"), "* -export-ignore -export-subst\n", cancellationToken).ConfigureAwait(false);

        // git archive: solo los archivos versionados de ese commit; nunca .git (ni la configuración del clon).
        var result = await RunGitToStreamAsync(path, ["archive", "--format=tar", baseSha], destination, ExportTimeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode != 0)
        {
            throw new RepositoryPreparationException($"git archive ha fallado: {Reason(result, token: null)}");
        }
    }

    /// <summary>Límite máximo de bytes extraídos desde el tar del Sandbox (256 MiB).</summary>
    private const long MaxWorkspaceTarBytes = 256L * 1024 * 1024;

    /// <summary>Límite máximo de entradas en el tar del Sandbox.</summary>
    private const int MaxWorkspaceTarEntries = 50_000;

    public async Task<DiffResult> ComputeDiffAsync(Guid runId, Stream workspaceTar, string baseSha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceTar);
        if (string.IsNullOrWhiteSpace(baseSha) || !IsSha(baseSha.Trim()))
        {
            throw new RepositoryPreparationException("el commit base para calcular el diff no es válido");
        }

        var normalizedBaseSha = baseSha.Trim();
        var path = WorkspacePathOf(runId);
        if (!Directory.Exists(path))
        {
            throw new RepositoryPreparationException("el árbol de trabajo de la Ejecución no existe");
        }

        var trackedOutput = await RunCheckedAsync(path, ["ls-tree", "-r", "--name-only", "-z", normalizedBaseSha], token: null, cancellationToken).ConfigureAwait(false);
        var trackedFiles = new HashSet<string>(trackedOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        await SyncWorkspaceTarAsync(path, workspaceTar, trackedFiles, cancellationToken).ConfigureAwait(false);

        var tempIndex = Path.Combine(Path.GetTempPath(), $"codenetix-index-{runId:N}-{Guid.NewGuid():N}");
        var gitEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_INDEX_FILE"] = tempIndex,
        };

        try
        {
            await RunCheckedAsync(path, ["read-tree", normalizedBaseSha], token: null, cancellationToken, gitEnv).ConfigureAwait(false);
            await RunCheckedAsync(path, ["add", "-N", "--all"], token: null, cancellationToken, gitEnv).ConfigureAwait(false);

            var diff = await RunCheckedAsync(path, ["diff", "--no-ext-diff", normalizedBaseSha], token: null, cancellationToken, gitEnv).ConfigureAwait(false);
            var changedRaw = await RunCheckedAsync(path, ["diff", "--no-ext-diff", "--no-renames", "--name-only", "-z", normalizedBaseSha], token: null, cancellationToken, gitEnv).ConfigureAwait(false);
            var changedFiles = changedRaw.Split('\0', StringSplitOptions.RemoveEmptyEntries);

            return new DiffResult(diff, [.. changedFiles]);
        }
        finally
        {
            try
            {
                if (File.Exists(tempIndex))
                {
                    File.Delete(tempIndex);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Limpieza de mejor esfuerzo del índice temporal.
            }
        }
    }

    /// <summary>
    /// Crea la rama <paramref name="branchName"/> exactamente sobre <paramref name="baseSha"/> con un único commit de
    /// los cambios sincronizados en el árbol de trabajo (verificando ausencia de <c>.git</c> y secretos) y hace push
    /// validado por <see cref="GitSafetyGuard"/> usando <c>http.extraHeader</c> efímero (AD-8).
    /// </summary>
    internal async Task<PushedBranch> CommitAndPushBranchAsync(
        Guid runId,
        string baseSha,
        string branchName,
        string commitMessage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseSha) || !IsSha(baseSha.Trim()))
        {
            throw new PullRequestPublishException("el commit base para crear la rama no es válido");
        }

        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            throw new PullRequestPublishException("el mensaje del commit está vacío");
        }

        var normalizedBaseSha = baseSha.Trim();
        var path = WorkspacePathOf(runId);
        if (!Directory.Exists(path))
        {
            throw new PullRequestPublishException("el árbol de trabajo de la Ejecución no existe");
        }

        var defaultBranch = await DetectDefaultBranchAsync(path, cancellationToken).ConfigureAwait(false);
        _safetyGuard.EnsurePushAllowed(branchName, defaultBranch);

        var token = options.Value.Token?.Trim();
        var normalizedBranch = branchName.Trim();
        if (normalizedBranch.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            normalizedBranch = normalizedBranch["refs/heads/".Length..];
        }

        // Fija la nueva rama exactamente en BaseSha sin tocar los cambios sincronizados del árbol de trabajo.
        await RunCheckedAsync(path, ["update-ref", $"refs/heads/{normalizedBranch}", normalizedBaseSha], token, cancellationToken).ConfigureAwait(false);
        await RunCheckedAsync(path, ["symbolic-ref", "HEAD", $"refs/heads/{normalizedBranch}"], token, cancellationToken).ConfigureAwait(false);
        await RunCheckedAsync(path, ["read-tree", normalizedBaseSha], token, cancellationToken).ConfigureAwait(false);
        await RunCheckedAsync(path, ["add", "-A"], token, cancellationToken).ConfigureAwait(false);

        // Verificar que no haya ningún archivo .git en el commit.
        var stagedRaw = await RunCheckedAsync(path, ["diff", "--cached", "--name-only", "-z"], token, cancellationToken).ConfigureAwait(false);
        foreach (var stagedPath in stagedRaw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ContainsGitSegment(stagedPath))
            {
                throw new PullRequestPublishException("el commit contiene archivos .git prohibidos");
            }
        }

        // Verificar que las líneas añadidas del diff preparado (incluidos binarios con --text) no contengan secretos.
        var stagedDiff = await RunCheckedAsync(path, ["diff", "--cached", "--no-ext-diff", "--text", "--unified=0"], token, cancellationToken).ConfigureAwait(false);
        foreach (var line in stagedDiff.Split('\n'))
        {
            if (!line.StartsWith('+') || line.StartsWith("+++", StringComparison.Ordinal))
            {
                continue;
            }

            if ((!string.IsNullOrEmpty(token) && line.Contains(token, StringComparison.Ordinal))
                || (_redactor is not null && !string.Equals(_redactor.Redact(line), line, StringComparison.Ordinal)))
            {
                throw new PullRequestPublishException("el commit contiene posibles secretos y ha sido bloqueado");
            }
        }

        var safeMessage = _redactor is null ? commitMessage.Trim() : _redactor.Redact(commitMessage.Trim());
        await RunCheckedAsync(
            path,
            [
                "-c", "user.name=CodeNetix",
                "-c", "user.email=codenetix@users.noreply.github.com",
                "-c", "commit.gpgsign=false",
                "commit", "--quiet", "--no-verify", "--allow-empty", "-m", safeMessage,
            ],
            token,
            cancellationToken).ConfigureAwait(false);

        // Verificar en un solo comando que HEAD es exactamente 1 commit cuyo único padre es BaseSha.
        var revListLine = (await RunCheckedAsync(path, ["rev-list", "--parents", "--max-count=1", "HEAD"], token, cancellationToken).ConfigureAwait(false)).Trim();
        var shas = revListLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (shas.Length != 2
            || !IsSha(shas[0])
            || !string.Equals(shas[1], normalizedBaseSha, StringComparison.Ordinal))
        {
            throw new PullRequestPublishException("el historial de la rama no tiene exactamente un único commit sobre BaseSha");
        }

        var commitSha = shas[0];

        // Segunda comprobación defensiva justo antes de ejecutar git push.
        _safetyGuard.EnsurePushAllowed(normalizedBranch, defaultBranch);

        var push = await RunGitAsync(
            path,
            ["push", "--quiet", "--no-verify", "--force-with-lease", "--no-tags", "--", "origin", $"refs/heads/{normalizedBranch}:refs/heads/{normalizedBranch}"],
            token,
            PushTimeout,
            cancellationToken).ConfigureAwait(false);
        if (push.TimedOut)
        {
            throw new PullRequestPublishException($"git push de {normalizedBranch} ha superado el tiempo máximo ({PushTimeout.TotalSeconds:0} s)");
        }

        if (push.ExitCode != 0)
        {
            var reason = Reason(push, token);
            if (_redactor is not null)
            {
                reason = _redactor.Redact(reason);
            }

            throw new PullRequestPublishException($"no se ha podido hacer push de {normalizedBranch}: {reason}");
        }

        await VerifyNoCredentialsAsync(path, token, cancellationToken).ConfigureAwait(false);
        LogPushed(normalizedBranch, commitSha, RunShortId.Of(runId));
        return new PushedBranch(normalizedBranch, defaultBranch, commitSha);
    }

    private async Task<string> DetectDefaultBranchAsync(string path, CancellationToken cancellationToken)
    {
        var originHead = await RunGitAsync(path, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], token: null, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (!originHead.TimedOut && originHead.ExitCode == 0)
        {
            var raw = originHead.StandardOutput.Trim();
            if (raw.StartsWith("origin/", StringComparison.Ordinal) && raw.Length > "origin/".Length)
            {
                return raw["origin/".Length..];
            }
        }

        var localHead = await RunGitAsync(path, ["symbolic-ref", "--short", "HEAD"], token: null, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (!localHead.TimedOut && localHead.ExitCode == 0)
        {
            var raw = localHead.StandardOutput.Trim();
            if (!string.IsNullOrWhiteSpace(raw)
                && !string.Equals(raw, "HEAD", StringComparison.Ordinal)
                && !raw.StartsWith(PullRequestBranch.Prefix, StringComparison.Ordinal))
            {
                return raw;
            }
        }

        throw new PullRequestPublishException("no se pudo determinar la rama por defecto del repositorio");
    }

    private static bool ContainsGitSegment(string relativePath)
    {
        foreach (var segment in relativePath.Replace('\\', '/').Split('/'))
        {
            if (string.Equals(segment.TrimEnd('.', ' '), ".git", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task SyncWorkspaceTarAsync(
        string workspaceRoot,
        Stream workspaceTar,
        IReadOnlySet<string> trackedFiles,
        CancellationToken cancellationToken)
    {
        if (workspaceTar.CanSeek && workspaceTar.Length - workspaceTar.Position == 0)
        {
            return;
        }

        var fullRoot = Path.GetFullPath(workspaceRoot);
        var fullRootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        bool? stripWorkspacePrefix = null;
        var clearedExisting = false;
        var entryCount = 0;
        long totalBytes = 0;
        var buffer = new byte[81920];

        try
        {
            await using var reader = new TarReader(workspaceTar, leaveOpen: true);
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (++entryCount > MaxWorkspaceTarEntries)
                {
                    throw new RepositoryPreparationException($"el tar del Sandbox supera el límite máximo de {MaxWorkspaceTarEntries} entradas");
                }

                // Ignorar metadatos PAX (p. ej. GlobalExtendedAttributes), enlaces y tipos especiales antes de evaluar el prefijo.
                if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    continue;
                }

                if (!clearedExisting)
                {
                    ClearWorkingTreeExceptGit(fullRoot);
                    clearedExisting = true;
                }

                var rawName = entry.Name.Replace('\\', '/');
                while (rawName.StartsWith("./", StringComparison.Ordinal))
                {
                    rawName = rawName[2..];
                }

                rawName = rawName.TrimStart('/').TrimEnd('/');
                if (rawName.Length == 0 || rawName == ".")
                {
                    continue;
                }

                stripWorkspacePrefix ??= rawName.Equals("workspace", StringComparison.Ordinal)
                    || rawName.StartsWith("workspace/", StringComparison.Ordinal);

                var relative = rawName;
                if (stripWorkspacePrefix.Value)
                {
                    if (relative.Equals("workspace", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (relative.StartsWith("workspace/", StringComparison.Ordinal))
                    {
                        relative = relative["workspace/".Length..];
                    }
                }

                if (relative.Length == 0)
                {
                    continue;
                }

                if (IsPathTraversalOrUnsafe(relative))
                {
                    throw new RepositoryPreparationException("el archivo tar del Sandbox contiene una ruta no segura");
                }

                if (IsExcludedWorkspacePath(relative, entry.EntryType == TarEntryType.Directory, trackedFiles))
                {
                    continue;
                }

                var destinationPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!destinationPath.StartsWith(fullRootPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    throw new RepositoryPreparationException("el archivo tar del Sandbox intenta escribir fuera del árbol de trabajo");
                }

                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(destinationPath);
                }
                else
                {
                    var parent = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    if (entry.DataStream is not null)
                    {
                        int read;
                        while ((read = await entry.DataStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            totalBytes += read;
                            if (totalBytes > MaxWorkspaceTarBytes)
                            {
                                throw new RepositoryPreparationException($"el árbol exportado del Sandbox supera el límite máximo de {MaxWorkspaceTarBytes / (1024 * 1024)} MiB");
                            }

                            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (RepositoryPreparationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new RepositoryPreparationException($"no se ha podido leer el tar del Sandbox: {ex.Message}", ex);
        }
    }

    private static bool IsPathTraversalOrUnsafe(string relativePath)
    {
        if (relativePath[0] is '/' or '\\' or '~'
            || relativePath.Contains(':', StringComparison.Ordinal)
            || relativePath.Any(char.IsControl))
        {
            return true;
        }

        foreach (var segment in relativePath.Split('/'))
        {
            // En Windows, segmentos terminados en '.' o espacio son normalizados por el FS (p. ej. ".git." → ".git").
            if (segment.Length == 0 || segment[^1] is '.' or ' ')
            {
                return true;
            }

            var normalized = segment.TrimEnd('.', ' ');
            if (normalized.Length == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExcludedWorkspacePath(string relativePath, bool isDirectory, IReadOnlySet<string> trackedFiles)
    {
        var hasBuildArtifactSegment = false;
        foreach (var segment in relativePath.Split('/'))
        {
            var normalized = segment.TrimEnd('.', ' ');
            if (string.Equals(normalized, ".git", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase))
            {
                hasBuildArtifactSegment = true;
            }
        }

        if (!hasBuildArtifactSegment)
        {
            return false;
        }

        // Conservar archivos/directorios bajo bin/ u obj/ que ya estaban versionados en BaseSha.
        if (trackedFiles.Contains(relativePath))
        {
            return false;
        }

        if (isDirectory)
        {
            var prefix = relativePath + "/";
            foreach (var tracked in trackedFiles)
            {
                if (tracked.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void ClearWorkingTreeExceptGit(string workspaceRoot)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(workspaceRoot))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                if (OperatingSystem.IsWindows())
                {
                    var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var file in Directory.EnumerateFiles(entry, "*", enumeration))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                }

                Directory.Delete(entry, recursive: true);
            }
            else if (File.Exists(entry))
            {
                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }

                File.Delete(entry);
            }
        }
    }

    public Task DeleteAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (TryWorkspacePathOf(runId) is { } path)
        {
            DeleteTree(path);
        }

        return Task.CompletedTask;
    }

    /// <summary><c>&lt;WorkspacesDirectory&gt;/&lt;shortId&gt;</c>.</summary>
    internal string WorkspacePathOf(Guid runId) =>
        TryWorkspacePathOf(runId) ?? throw new RepositoryPreparationException("no hay directorio de árboles de trabajo configurado");

    private string? TryWorkspacePathOf(Guid runId)
    {
        var root = options.Value.WorkspacesDirectory;
        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(Path.GetFullPath(root), RunShortId.Of(runId));
    }

    /// <summary><c>&lt;CloneBaseUrl&gt;/&lt;owner&gt;/&lt;repo&gt;.git</c>; la base no puede llevar credenciales.</summary>
    internal string CloneUrlOf(string owner, string repo)
    {
        if (!IsName(owner) || !IsName(repo))
        {
            throw new RepositoryPreparationException("el nombre del repositorio no es válido");
        }

        var configured = options.Value.CloneBaseUrl;
        var raw = string.IsNullOrWhiteSpace(configured) ? GitHubOptions.DefaultCloneBaseUrl : configured.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("https" or "http" or "file")
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new RepositoryPreparationException("CODENETIX__GitHub__CloneBaseUrl no es una URL https, http o file válida sin credenciales");
        }

        return $"{baseUri.AbsoluteUri.TrimEnd('/')}/{owner}/{repo}.git";
    }

    private async Task<PreparedRepository> CloneAndReadAsync(string path, string cloneUrl, string owner, string repo, CancellationToken cancellationToken)
    {
        var token = options.Value.Token?.Trim();

        // 1) Clon superficial de la rama por defecto, con la credencial solo en el entorno del proceso.
        var clone = await RunGitAsync(
            workingDirectory: null,
            ["clone", "--quiet", "--depth", "1", "--no-tags", "--single-branch", "--", cloneUrl, path],
            token,
            CloneTimeout,
            cancellationToken).ConfigureAwait(false);
        if (clone.TimedOut)
        {
            throw new RepositoryPreparationException(
                $"el clon de {owner}/{repo} ha superado el tiempo máximo ({CloneTimeout.TotalSeconds:0} s)");
        }

        if (clone.ExitCode != 0)
        {
            throw new RepositoryPreparationException($"no se ha podido clonar {owner}/{repo}: {Reason(clone, token)}");
        }

        // 2) BaseSha.
        var head = await RunCheckedAsync(path, ["rev-parse", "--verify", "HEAD"], token, cancellationToken).ConfigureAwait(false);
        var baseSha = head.Trim();
        if (!IsSha(baseSha))
        {
            throw new RepositoryPreparationException($"no se ha podido fijar el commit base de {owner}/{repo}");
        }

        // 3) Ninguna credencial en el árbol de trabajo.
        await VerifyNoCredentialsAsync(path, token, cancellationToken).ConfigureAwait(false);

        // 4) codenetix.yml desde el BaseSha (no desde el árbol de trabajo, que podría no coincidir).
        var yaml = await ReadRepoConfigAsync(path, baseSha, token, cancellationToken).ConfigureAwait(false);
        return new PreparedRepository(path, baseSha, yaml);
    }

    /// <summary>
    /// Lanza <see cref="RepositoryPreparationException"/> si la configuración local del clon contiene
    /// <c>extraheader</c> o el token, o si la URL de <c>origin</c> lleva userinfo.
    /// </summary>
    internal async Task VerifyNoCredentialsAsync(string path, string? token, CancellationToken cancellationToken)
    {
        const string Leaked = "el clon conserva credenciales en su configuración; se ha descartado";

        var local = await RunCheckedAsync(path, ["config", "--list", "--local"], token, cancellationToken).ConfigureAwait(false);
        if (local.Contains("extraheader", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(token) && local.Contains(token, StringComparison.Ordinal)))
        {
            throw new RepositoryPreparationException(Leaked);
        }

        var configFile = Path.Combine(path, ".git", "config");
        if (!string.IsNullOrEmpty(token) && File.Exists(configFile)
            && (await File.ReadAllTextAsync(configFile, cancellationToken).ConfigureAwait(false)).Contains(token, StringComparison.Ordinal))
        {
            throw new RepositoryPreparationException(Leaked);
        }

        var origin = (await RunCheckedAsync(path, ["remote", "get-url", "origin"], token, cancellationToken).ConfigureAwait(false)).Trim();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !string.IsNullOrEmpty(originUri.UserInfo))
        {
            throw new RepositoryPreparationException(Leaked);
        }
    }

    /// <summary>Contenido de <c>codenetix.yml</c> en <paramref name="baseSha"/>, o <c>null</c> si no hay un archivo con ese nombre.</summary>
    internal async Task<string?> ReadRepoConfigAsync(string path, string baseSha, string? token, CancellationToken cancellationToken)
    {
        var entry = await RunCheckedAsync(path, ["ls-tree", baseSha, "--", RepoConfigFileName], token, cancellationToken).ConfigureAwait(false);

        // "<modo> <tipo> <objeto>\t<ruta>": solo un blob (archivo o enlace simbólico) es la configuración.
        if (string.IsNullOrWhiteSpace(entry) || !entry.Contains(" blob ", StringComparison.Ordinal))
        {
            return null;
        }

        return await RunCheckedAsync(path, ["show", $"{baseSha}:{RepoConfigFileName}"], token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> RunCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string? token,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var result = await RunGitAsync(workingDirectory, arguments, token: null, CommandTimeout, cancellationToken, extraEnvironment).ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode != 0)
        {
            throw new RepositoryPreparationException($"git {arguments[0]} ha fallado: {Reason(result, token)}");
        }

        return result.StandardOutput;
    }

    /// <summary>
    /// Ejecuta <c>git</c> con un entorno limpio: sin variables <c>GIT_*</c> heredadas (p. ej. <c>GIT_TRACE_CURL</c>,
    /// que volcaría cabeceras), sin preguntas por terminal y sin ayudantes de credenciales. Con <paramref name="token"/>,
    /// añade <c>http.extraHeader</c> por <c>GIT_CONFIG_*</c> (solo para el clon/push).
    /// Bloquea cualquier subcomando <c>merge</c> a través de <see cref="GitSafetyGuard"/>.
    /// </summary>
    internal async Task<GitResult> RunGitAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        string? token,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        if (IsMergeSubcommand(arguments))
        {
            _safetyGuard.EnsureMergeForbidden("git merge");
        }

        using var process = StartGit(workingDirectory, arguments, token, binaryOutput: false, extraEnvironment);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        if (!await WaitForExitAsync(process, [stdout, stderr], timeout, cancellationToken).ConfigureAwait(false))
        {
            return new GitResult(-1, string.Empty, string.Empty, TimedOut: true);
        }

        return new GitResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), TimedOut: false);
    }

    private static bool IsMergeSubcommand(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg is "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace" or "--super-prefix")
            {
                i++;
                continue;
            }

            if (arg.StartsWith('-'))
            {
                continue;
            }

            return string.Equals(arg, "merge", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Como <see cref="RunGitAsync"/> (mismo entorno saneado, sin token), pero copia la salida estándar binaria a
    /// <paramref name="destination"/> sin acumularla en memoria (<c>git archive</c>).
    /// </summary>
    private static async Task<GitResult> RunGitToStreamAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        Stream destination,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = StartGit(workingDirectory, arguments, token: null, binaryOutput: true);
        using var copySource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var copy = process.StandardOutput.BaseStream.CopyToAsync(destination, copySource.Token);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        bool exited;
        try
        {
            exited = await WaitForExitAsync(process, [copy, stderr], timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await copySource.CancelAsync().ConfigureAwait(false);
            throw;
        }

        if (!exited)
        {
            await copySource.CancelAsync().ConfigureAwait(false);
            return new GitResult(-1, string.Empty, string.Empty, TimedOut: true);
        }

        // La salida puede seguir en la tubería tras el fin del proceso: se termina de copiar.
        await copy.ConfigureAwait(false);
        return new GitResult(process.ExitCode, string.Empty, await stderr.ConfigureAwait(false), TimedOut: false);
    }

    /// <summary>
    /// Espera a que termine <paramref name="process"/>. Con timeout o cancelación lo mata y espera (5 s como mucho)
    /// a sus lecturas; devuelve <c>false</c> si ha vencido el timeout y relanza la cancelación.
    /// </summary>
    private static async Task<bool> WaitForExitAsync(Process process, Task[] readers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            try
            {
                // Un nieto que herede las tuberías podría mantenerlas abiertas: no se espera más de 5 s.
                await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                // Se abandona la lectura.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>
    /// Arranca <c>git</c> con un entorno limpio (ver <see cref="RunGitAsync"/>). Con <paramref name="binaryOutput"/>,
    /// la salida estándar se lee como bytes (<c>BaseStream</c>).
    /// </summary>
    private static Process StartGit(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        string? token,
        bool binaryOutput,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = binaryOutput ? null : new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        if (workingDirectory is not null)
        {
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(workingDirectory);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var key in startInfo.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment.Remove("SSH_ASKPASS");
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";
        startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";

        // Sin configuración del sistema ni global del host (hooksPath, insteadOf, include…): solo la de abajo.
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

        var config = new List<(string Key, string Value)>
        {
            // Lista de ayudantes vacía: no se consulta ningún almacén de credenciales del sistema.
            ("credential.helper", string.Empty),
        };
        if (!string.IsNullOrEmpty(token))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
            config.Add(("http.extraHeader", $"Authorization: Basic {basic}"));
        }

        startInfo.Environment["GIT_CONFIG_COUNT"] = config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < config.Count; i++)
        {
            startInfo.Environment[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
            startInfo.Environment[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
        }

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            process.Dispose();
            throw new RepositoryPreparationException("no se encuentra el ejecutable git");
        }

        process.StandardInput.Close();
        return process;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Ya había terminado.
        }
    }

    /// <summary>Última línea útil de <c>stderr</c>, sin el token ni la cabecera, en una línea y acotada.</summary>
    private static string Reason(GitResult result, string? token)
    {
        var lines = result.StandardError
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .ToList();
        var reason = lines.Count == 0 ? $"git terminó con código {result.ExitCode}" : string.Join(' ', lines.TakeLast(2));

        if (!string.IsNullOrEmpty(token))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
            reason = reason
                .Replace(basic, "***", StringComparison.Ordinal)
                .Replace(token, "***", StringComparison.Ordinal)
                .Replace(Uri.EscapeDataString(token), "***", StringComparison.Ordinal);
        }

        return reason.Length <= MaxReasonLength ? reason : string.Concat(reason.AsSpan(0, MaxReasonLength - 1), "…");
    }

    private static bool IsSha(string value) =>
        value.Length is 40 or 64 && value.All(char.IsAsciiHexDigitLower);

    private static bool IsName(string? value) => value is not null && GitHubObjectKey.IsName(value);

    /// <summary>
    /// Borra el árbol (de mejor esfuerzo, nunca lanza). En Windows los objetos de <c>.git</c> son de solo lectura:
    /// se les quita el atributo antes de borrar; se reintenta brevemente por si un proceso <c>git</c> recién
    /// terminado aún retiene algún archivo.
    /// </summary>
    private void DeleteTree(string path)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                if (OperatingSystem.IsWindows())
                {
                    // Sin seguir enlaces simbólicos: nunca se toca nada fuera del árbol.
                    var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var file in Directory.EnumerateFiles(path, "*", enumeration))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                {
                    LogDeleteFailed(path, ex.Message);
                    return;
                }

                Thread.Sleep(100 * attempt);
            }
        }
    }

    internal sealed record GitResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

    [LoggerMessage(Level = LogLevel.Information, Message = "Repositorio {Owner}/{Repo} clonado en el commit {BaseSha} (Ejecución {ShortId}).")]
    private partial void LogPrepared(string owner, string repo, string baseSha, string shortId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rama {Branch} publicada con commit {CommitSha} (Ejecución {ShortId}).")]
    private partial void LogPushed(string branch, string commitSha, string shortId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido borrar el árbol de trabajo '{Path}': {Reason}.")]
    private partial void LogDeleteFailed(string path, string reason);
}
