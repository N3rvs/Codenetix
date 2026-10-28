using System.Diagnostics;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Repositorios git locales para probar el clonado: <c>&lt;Root&gt;/&lt;owner&gt;/&lt;repo&gt;.git</c> (con árbol de trabajo),
/// de modo que <c>CODENETIX__GitHub__CloneBaseUrl = <see cref="BaseUrl"/></c> los resuelve como
/// <c>&lt;base&gt;/&lt;owner&gt;/&lt;repo&gt;.git</c>. Se borran al liberar.
/// </summary>
internal sealed class LocalGitRepos : IDisposable
{
    private static readonly Lazy<LocalGitRepos> SharedDefault = new(CreateDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    public LocalGitRepos()
    {
        Root = Path.Combine(Path.GetTempPath(), "codenetix-repos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary><c>file:///…</c> de <see cref="Root"/>.</summary>
    public string BaseUrl => new Uri(Root + Path.DirectorySeparatorChar).AbsoluteUri;

    /// <summary>
    /// Repositorios por defecto del proceso de pruebas (<c>o/r</c>, sin <c>codenetix.yml</c>): así los tests que
    /// solo envían Eventos nunca clonan de la red. No se borran (viven en el directorio temporal).
    /// </summary>
    public static LocalGitRepos Default => SharedDefault.Value;

    /// <summary>Crea <c>owner/repo</c> con <paramref name="files"/> en un único commit; devuelve el SHA.</summary>
    public string Create(string owner, string repo, IReadOnlyDictionary<string, string>? files = null)
    {
        var path = PathOf(owner, repo);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        files ??= new Dictionary<string, string> { ["README.md"] = "# prueba\n" };
        foreach (var (name, content) in files)
        {
            var file = Path.Combine(path, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }

        Git(path, "add", "-A");
        Git(path, "commit", "-q", "-m", "inicial");
        return Git(path, "rev-parse", "HEAD").Trim();
    }

    /// <summary>Añade un commit que escribe <paramref name="name"/>; devuelve el SHA.</summary>
    public string Commit(string owner, string repo, string name, string content)
    {
        var path = PathOf(owner, repo);
        File.WriteAllText(Path.Combine(path, name), content);
        Git(path, "add", "-A");
        Git(path, "commit", "-q", "-m", "cambio");
        return Git(path, "rev-parse", "HEAD").Trim();
    }

    public string PathOf(string owner, string repo) => Path.Combine(Root, owner, repo + ".git");

    /// <summary>Ejecuta git en <paramref name="workingDirectory"/> con identidad fija y sin configuración global.</summary>
    public static string Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var setting in new[] { "user.name=CodeNetix Tests", "user.email=tests@codenetix.invalid", "commit.gpgsign=false", "core.autocrlf=false" })
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(setting);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} falló ({process.ExitCode}): {stderr}");
        }

        return stdout.GetAwaiter().GetResult();
    }

    private static LocalGitRepos CreateDefault()
    {
        var repos = new LocalGitRepos();
        repos.Create("o", "r");
        return repos;
    }

    public void Dispose() => DeleteDirectory(Root);

    /// <summary>Borrado de mejor esfuerzo (en Windows los objetos de <c>.git</c> son de solo lectura).</summary>
    public static void DeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }
}
