using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas estructurales sobre la documentación de lanzamiento (<c>SECURITY.md</c>, <c>LICENSE</c>,
/// <c>CONTRIBUTING.md</c>, <c>README.md</c>) y su inclusión en <c>docker/codenetix.Dockerfile</c>
/// (Historia 6.5).
/// </summary>
public sealed class ReleaseDocumentationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Security_md_defines_private_disclosure_supported_versions_and_operator_responsibilities()
    {
        var securityPath = FindRepoFile("SECURITY.md");
        var content = await File.ReadAllTextAsync(securityPath, Ct);

        Assert.Matches(
            new Regex(@"nunca\s+abras\s+una\s+issue\s+p[úu]blica|no\s+abras\s+issues\s+p[úu]blicas", RegexOptions.IgnoreCase),
            content);
        Assert.Contains("https://github.com/N3rvs/codenetix/security/advisories/new", content, StringComparison.Ordinal);
        Assert.Contains("72", content, StringComparison.Ordinal);
        Assert.Contains("7 días", content, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Versiones soportadas", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vX.Y.Z", content, StringComparison.Ordinal);

        Assert.Contains("/var/run/docker.sock", content, StringComparison.Ordinal);
        Assert.Contains("Sandbox", content, StringComparison.Ordinal);
        Assert.Contains("internal: true", content, StringComparison.Ordinal);
        Assert.Contains(".git", content, StringComparison.Ordinal);
        Assert.Contains("no-new-privileges", content, StringComparison.Ordinal);
        Assert.Contains("Egress Gateway", content, StringComparison.Ordinal);
        Assert.Contains("BYOK", content, StringComparison.Ordinal);
        Assert.Contains("admin-token", content, StringComparison.Ordinal);
        Assert.Contains("cosign", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SBOM", content, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/n3rvs/", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task License_is_apache_2_0_and_contributing_requires_dco_and_references_security()
    {
        var licensePath = FindRepoFile("LICENSE");
        var license = await File.ReadAllTextAsync(licensePath, Ct);

        Assert.Contains("Apache License", license, StringComparison.Ordinal);
        Assert.Contains("Version 2.0", license, StringComparison.Ordinal);

        var contributingPath = FindRepoFile("CONTRIBUTING.md");
        var contributing = await File.ReadAllTextAsync(contributingPath, Ct);

        Assert.Contains("Developer Certificate of Origin", contributing, StringComparison.Ordinal);
        Assert.Contains("git commit -s", contributing, StringComparison.Ordinal);
        Assert.Contains("Signed-off-by:", contributing, StringComparison.Ordinal);
        Assert.Contains("[SECURITY.md](SECURITY.md)", contributing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readme_documents_quickstart_cosign_security_byok_and_credits_without_obsolete_notice_or_ghcr_latest()
    {
        var defaultVersion = await ReadActionDefaultVersionAsync();
        var readmePath = FindRepoFile("README.md");
        var readme = await File.ReadAllTextAsync(readmePath, Ct);

        Assert.DoesNotContain("llegan en la próxima épica", readme, StringComparison.OrdinalIgnoreCase);

        // Quickstart (< 10 min, SM-1) con imágenes firmadas semver (nunca ghcr.io/n3rvs/...:latest)
        Assert.Contains("docker run", readme, StringComparison.Ordinal);
        Assert.Contains("-p 127.0.0.1:5000:5000", readme, StringComparison.Ordinal);
        Assert.Contains($"ghcr.io/n3rvs/codenetix:{defaultVersion}", readme, StringComparison.Ordinal);
        Assert.Contains($"ghcr.io/n3rvs/codenetix-sandbox:{defaultVersion}", readme, StringComparison.Ordinal);
        Assert.Contains($"docker pull ghcr.io/n3rvs/codenetix-sandbox:{defaultVersion}", readme, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"ghcr\.io/n3rvs/[^\s`""']*[:@]latest\b", RegexOptions.IgnoreCase),
            readme);
        Assert.Contains("codenetix fix", readme, StringComparison.Ordinal);
        Assert.Contains("docker exec codenetix codenetix fix", readme, StringComparison.Ordinal);
        Assert.Contains("--follow", readme, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:5000", readme, StringComparison.Ordinal);
        Assert.Contains("docker build -f docker/codenetix.Dockerfile", readme, StringComparison.Ordinal);
        Assert.Contains("docker build -f docker/sandbox.Dockerfile", readme, StringComparison.Ordinal);

        // Todas las referencias concretas (no marcadores como <tag>) a imágenes GHCR usan la etiqueta de action.yml.
        var ghcrTags = Regex.Matches(readme, @"ghcr\.io/n3rvs/(?:codenetix|codenetix-sandbox):(v\d[^\s`""'\\]*)")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.NotEmpty(ghcrTags);
        Assert.All(ghcrTags, tag => Assert.Equal(defaultVersion, tag));
        Assert.Contains($"imagetools inspect ghcr.io/n3rvs/codenetix:{defaultVersion}", readme, StringComparison.Ordinal);
        Assert.Contains($"imagetools inspect ghcr.io/n3rvs/codenetix-sandbox:{defaultVersion}", readme, StringComparison.Ordinal);
        Assert.Contains("/api/health", readme, StringComparison.Ordinal);
        Assert.Contains("/data/admin.token", readme, StringComparison.Ordinal);
        Assert.Contains("CODENETIX__Host__AccessToken", readme, StringComparison.Ordinal);

        // Verificación de firmas cosign + SBOM
        Assert.Contains("cosign verify", readme, StringComparison.Ordinal);
        Assert.Contains("--certificate-identity-regexp", readme, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"https://github\\?\.com/N3rvs/codenetix/\\?\.github/workflows/release\\?\.yml@refs/tags/v", RegexOptions.CultureInvariant),
            readme);
        Assert.Contains("--certificate-oidc-issuer", readme, StringComparison.Ordinal);
        Assert.Contains("https://token.actions.githubusercontent.com", readme, StringComparison.Ordinal);
        Assert.Contains("docker buildx imagetools inspect", readme, StringComparison.Ordinal);
        Assert.Contains("SBOM", readme, StringComparison.Ordinal);

        // Seguridad, riesgos y uso de keys propias (BYOK) + presupuesto, acotado a su propia sección
        var securitySection = Regex.Match(
            readme,
            @"^## Seguridad, riesgos y uso de keys propias \(BYOK\)\s*$(?<body>.*?)(?=^## |\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(securitySection.Success, "README.md debe tener la sección '## Seguridad, riesgos y uso de keys propias (BYOK)'.");
        var security = securitySection.Groups["body"].Value;
        Assert.Matches(new Regex(@"socket de Docker", RegexOptions.IgnoreCase), security);
        Assert.Contains("/var/run/docker.sock", security, StringComparison.Ordinal);
        Assert.Contains("root", security, StringComparison.Ordinal);
        Assert.Contains("internal: true", security, StringComparison.Ordinal);
        Assert.Contains(".git", security, StringComparison.Ordinal);
        Assert.Contains("no-new-privileges", security, StringComparison.Ordinal);
        Assert.Contains("Egress", security, StringComparison.Ordinal);
        Assert.Contains(":8080/llm", security, StringComparison.Ordinal);
        Assert.Contains(":8081", security, StringComparison.Ordinal);
        Assert.Contains("Category=SecurityFire", security, StringComparison.Ordinal);
        Assert.Contains("BYOK", security, StringComparison.Ordinal);
        Assert.Contains("CODENETIX__Limits__BudgetUsd", security, StringComparison.Ordinal);
        Assert.Contains("CODENETIX__Pricing__", security, StringComparison.Ordinal);
        Assert.Contains("402", security, StringComparison.Ordinal);
        Assert.Contains("--max-budget-usd", security, StringComparison.Ordinal);
        Assert.Contains("[SECURITY.md](SECURITY.md)", security, StringComparison.Ordinal);

        // Créditos, contribución (DCO) y licencia
        Assert.Contains("Eric Risco", readme, StringComparison.Ordinal);
        Assert.Contains("[CONTRIBUTING.md](CONTRIBUTING.md)", readme, StringComparison.Ordinal);
        Assert.Contains("git commit -s", readme, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"\[[^\]]+\]\(LICENSE\)"), readme);
        Assert.Contains("Apache 2.0", readme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codenetix_dockerfile_copies_release_documents_before_running_dotnet_test()
    {
        var dockerfilePath = FindRepoFile(Path.Combine("docker", "codenetix.Dockerfile"));
        var lines = (await File.ReadAllLinesAsync(dockerfilePath, Ct))
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
            .ToList();

        var buildStageStart = lines.FindIndex(l =>
            l.StartsWith("FROM ", StringComparison.Ordinal)
            && l.Contains(" AS build", StringComparison.Ordinal));
        Assert.True(buildStageStart >= 0, "docker/codenetix.Dockerfile debe declarar una etapa 'AS build'.");

        var nextStageStart = lines.FindIndex(buildStageStart + 1, l => l.StartsWith("FROM ", StringComparison.Ordinal));
        var buildStageLines = nextStageStart >= 0
            ? lines.GetRange(buildStageStart, nextStageStart - buildStageStart)
            : lines.GetRange(buildStageStart, lines.Count - buildStageStart);

        var dotnetTestIndex = buildStageLines.FindIndex(l =>
            l.Contains("dotnet test", StringComparison.Ordinal));
        Assert.True(dotnetTestIndex >= 0, "La etapa 'AS build' debe ejecutar 'dotnet test'.");

        foreach (var docFile in new[] { "SECURITY.md", "LICENSE", "CONTRIBUTING.md", "README.md", "action.yml" })
        {
            var copyIndex = buildStageLines.FindIndex(l =>
                l.StartsWith("COPY ", StringComparison.Ordinal)
                && l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(docFile, StringComparer.Ordinal));
            Assert.True(copyIndex >= 0, $"La etapa 'AS build' debe copiar '{docFile}'.");
            Assert.True(dotnetTestIndex > copyIndex, $"La etapa 'AS build' debe copiar '{docFile}' antes de ejecutar 'dotnet test'.");
        }

        if (TryFindRepoFile(".dockerignore", out var dockerIgnorePath))
        {
            var ignoreLines = (await File.ReadAllLinesAsync(dockerIgnorePath!, Ct))
                .Select(l => l.Trim())
                .ToArray();
            Assert.DoesNotContain("SECURITY.md", ignoreLines, StringComparer.Ordinal);
            Assert.DoesNotContain("LICENSE", ignoreLines, StringComparer.Ordinal);
            Assert.DoesNotContain("CONTRIBUTING.md", ignoreLines, StringComparer.Ordinal);
            Assert.DoesNotContain("README.md", ignoreLines, StringComparer.Ordinal);
            Assert.DoesNotContain("action.yml", ignoreLines, StringComparer.Ordinal);
        }
    }

    private static async Task<string> ReadActionDefaultVersionAsync()
    {
        var actionPath = FindRepoFile("action.yml");
        var yaml = await File.ReadAllTextAsync(actionPath, Ct);
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);

        var root = Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
        var inputs = GetRequiredMapping(root, "inputs");
        var version = GetRequiredMapping(inputs, "version");
        return GetRequiredScalar(version, "default");
    }

    private static YamlMappingNode GetRequiredMapping(YamlMappingNode mapping, string key)
    {
        foreach (var entry in mapping.Children)
        {
            if (entry.Key is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.Ordinal)
                && entry.Value is YamlMappingNode result)
            {
                return result;
            }
        }

        throw new Xunit.Sdk.XunitException($"Se esperaba un objeto YAML en la clave '{key}'.");
    }

    private static string GetRequiredScalar(YamlMappingNode mapping, string key)
    {
        foreach (var entry in mapping.Children)
        {
            if (entry.Key is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.Ordinal)
                && entry.Value is YamlScalarNode value
                && value.Value is not null)
            {
                return value.Value;
            }
        }

        throw new Xunit.Sdk.XunitException($"Se esperaba un escalar YAML en la clave '{key}'.");
    }

    private static bool TryFindRepoFile(string relativePath, out string? fullPath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                fullPath = candidate;
                return true;
            }

            dir = dir.Parent;
        }

        fullPath = null;
        return false;
    }

    private static string FindRepoFile(string relativePath)
    {
        if (TryFindRepoFile(relativePath, out var fullPath))
        {
            return fullPath!;
        }

        throw new FileNotFoundException($"No se encontró '{relativePath}' ascendiendo desde '{AppContext.BaseDirectory}'.");
    }
}
