using System.Xml.Linq;

namespace CodeNetix.ArchitectureTests;

/// <summary>Un proyecto de <c>src/</c> y los nombres de los proyectos que referencia.</summary>
public sealed record ProjectInfo(string Name, IReadOnlyList<string> References);

/// <summary>
/// Regla de AD-1: Contracts no referencia nada; los módulos de negocio solo referencian
/// Contracts; solo Host puede referenciar al resto. Se basa en los <c>ProjectReference</c>
/// de los <c>.csproj</c> (determinista: el compilador elimina referencias de ensamblado sin uso).
/// </summary>
public static class ModuleDependencyRule
{
    public const string Contracts = "CodeNetix.Contracts";
    public const string Host = "CodeNetix.Host";

    public static readonly IReadOnlyList<string> ExpectedProjects =
    [
        "CodeNetix.Agents",
        "CodeNetix.Contracts",
        "CodeNetix.GitHub",
        "CodeNetix.Host",
        "CodeNetix.Ingestion",
        "CodeNetix.Persistence",
        "CodeNetix.Pipeline",
        "CodeNetix.Sandbox",
    ];

    public static IReadOnlyList<string> FindViolations(IEnumerable<ProjectInfo> projects)
    {
        var violations = new List<string>();
        foreach (var project in projects)
        {
            if (project.Name == Host)
            {
                continue;
            }

            foreach (var reference in project.References)
            {
                if (project.Name == Contracts)
                {
                    violations.Add($"{project.Name} -> {reference}: Contracts no puede referenciar ningún proyecto.");
                }
                else if (reference != Contracts)
                {
                    violations.Add($"{project.Name} -> {reference}: un módulo solo puede referenciar {Contracts} (AD-1).");
                }
            }
        }

        return violations;
    }

    public static string FindSolutionRoot(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeNetix.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No se encontró CodeNetix.slnx subiendo desde '{startDirectory}'.");
    }

    public static IReadOnlyList<ProjectInfo> LoadSourceProjects(string solutionRoot)
    {
        var src = Path.Combine(solutionRoot, "src");
        return Directory.EnumerateDirectories(src)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly))
            .Select(LoadProject)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static ProjectInfo LoadProject(string csprojPath)
    {
        var document = XDocument.Load(csprojPath);
        var references = document.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .ToList();

        return new ProjectInfo(Path.GetFileNameWithoutExtension(csprojPath), references);
    }
}
