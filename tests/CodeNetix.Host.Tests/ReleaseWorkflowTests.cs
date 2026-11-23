using System.Text.Json;
using System.Text.RegularExpressions;
using CodeNetix.Contracts;
using YamlDotNet.RepresentationModel;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas estructurales sobre el workflow de release firmado (<c>.github/workflows/release.yml</c>),
/// la configuración de Renovate (<c>renovate.json</c>) y su inclusión en <c>docker/codenetix.Dockerfile</c>
/// (Historia 6.4).
/// </summary>
public sealed class ReleaseWorkflowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Release_workflow_triggers_only_on_semver_tags_uses_minimum_permissions_and_avoids_run_interpolation()
    {
        var workflowPath = FindRepoFile(Path.Combine(".github", "workflows", "release.yml"));
        var root = ParseYamlMapping(await File.ReadAllTextAsync(workflowPath, Ct));

        Assert.Equal("Release", GetRequiredScalar(root, "name"));

        var onNode = GetRequiredMapping(root, "on");
        Assert.Single(onNode.Children);
        Assert.True(TryGetMapping(onNode, "push", out var pushNode), "El workflow debe dispararse únicamente en 'push'.");
        Assert.Single(pushNode!.Children);
        Assert.False(TryGetNode(pushNode, "branches", out _), "El workflow de release no debe dispararse en ramas.");
        Assert.False(TryGetNode(onNode, "pull_request_target", out _), "El workflow de release nunca debe usar 'pull_request_target'.");

        var tags = GetRequiredSequence(pushNode, "tags")
            .Children
            .OfType<YamlScalarNode>()
            .Select(n => n.Value)
            .ToArray();
        Assert.Single(tags);
        Assert.Equal("v[0-9]+.[0-9]+.[0-9]+", tags[0]);

        var rootPermissions = GetRequiredMapping(root, "permissions");
        Assert.Single(rootPermissions.Children);
        Assert.Equal("read", GetRequiredScalar(rootPermissions, "contents"));
        AssertNoWritePermissions(rootPermissions, "workflow");

        var jobs = GetRequiredMapping(root, "jobs");
        Assert.Equal(2, jobs.Children.Count);

        var verifyJob = GetRequiredMapping(jobs, "verify");
        var verifyPermissions = GetRequiredMapping(verifyJob, "permissions");
        Assert.Single(verifyPermissions.Children);
        Assert.Equal("read", GetRequiredScalar(verifyPermissions, "contents"));
        AssertNoWritePermissions(verifyPermissions, "job 'verify'");

        var publishJob = GetRequiredMapping(jobs, "publish-images");
        var publishNeeds = GetRequiredSequence(publishJob, "needs")
            .Children
            .OfType<YamlScalarNode>()
            .Select(n => n.Value)
            .ToArray();
        Assert.Contains("verify", publishNeeds, StringComparer.Ordinal);

        var publishPermissions = GetRequiredMapping(publishJob, "permissions");
        Assert.Equal(4, publishPermissions.Children.Count);
        Assert.Equal("read", GetRequiredScalar(publishPermissions, "contents"));
        Assert.Equal("write", GetRequiredScalar(publishPermissions, "packages"));
        Assert.Equal("write", GetRequiredScalar(publishPermissions, "id-token"));
        Assert.Equal("write", GetRequiredScalar(publishPermissions, "attestations"));

        foreach (var entry in jobs.Children)
        {
            var jobName = Assert.IsType<YamlScalarNode>(entry.Key).Value!;
            var job = Assert.IsType<YamlMappingNode>(entry.Value);

            Assert.Equal("ubuntu-latest", GetRequiredScalar(job, "runs-on"));
            Assert.True(
                int.TryParse(GetRequiredScalar(job, "timeout-minutes"), out var timeout) && timeout > 0,
                $"El job '{jobName}' debe declarar un 'timeout-minutes' positivo.");

            var steps = GetRequiredSequence(job, "steps").Children.OfType<YamlMappingNode>();
            foreach (var step in steps)
            {
                if (TryGetScalar(step, "continue-on-error", out var continueOnError) && continueOnError is not null)
                {
                    Assert.False(
                        string.Equals(continueOnError, "true", StringComparison.OrdinalIgnoreCase),
                        $"Ningún paso del job '{jobName}' puede declarar 'continue-on-error: true'.");
                }

                if (TryGetScalar(step, "run", out var run) && run is not null)
                {
                    Assert.DoesNotContain("${{", run, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public async Task Verify_job_validates_semver_tag_panel_dotnet_architecture_sandbox_host_tests_and_security_fire()
    {
        var workflowPath = FindRepoFile(Path.Combine(".github", "workflows", "release.yml"));
        var root = ParseYamlMapping(await File.ReadAllTextAsync(workflowPath, Ct));
        var jobs = GetRequiredMapping(root, "jobs");
        var verifyJob = GetRequiredMapping(jobs, "verify");
        var steps = GetRequiredSequence(verifyJob, "steps").Children.OfType<YamlMappingNode>().ToList();

        Assert.Contains(
            steps,
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("actions/checkout@", StringComparison.Ordinal));

        var tagValidationStep = Assert.Single(
            steps,
            s => TryGetMapping(s, "env", out var env)
                && env is not null
                && TryGetScalar(env, "TAG_NAME", out var tagExpr)
                && string.Equals(tagExpr, "${{ github.ref_name }}", StringComparison.Ordinal));
        var tagValidationRun = GetRequiredScalar(tagValidationStep, "run");
        Assert.Contains("^v(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", tagValidationRun, StringComparison.Ordinal);
        Assert.DoesNotContain("${{", tagValidationRun, StringComparison.Ordinal);

        var setupNodeStep = Assert.Single(
            steps,
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("actions/setup-node@", StringComparison.Ordinal));
        var setupNodeWith = GetRequiredMapping(setupNodeStep, "with");
        Assert.Equal("24", GetRequiredScalar(setupNodeWith, "node-version"));
        Assert.Equal("npm", GetRequiredScalar(setupNodeWith, "cache"));
        Assert.Equal("panel/package-lock.json", GetRequiredScalar(setupNodeWith, "cache-dependency-path"));

        var setupDotnetStep = Assert.Single(
            steps,
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("actions/setup-dotnet@", StringComparison.Ordinal));
        var setupDotnetWith = GetRequiredMapping(setupDotnetStep, "with");
        Assert.Equal("global.json", GetRequiredScalar(setupDotnetWith, "global-json-file"));

        var runCommands = steps
            .Select(s => TryGetScalar(s, "run", out var run) ? run : null)
            .Where(run => run is not null)
            .Cast<string>()
            .ToList();

        var panelCiIndex = FindCommandIndex(runCommands, "npm --prefix panel ci");
        var panelCheckApiIndex = FindCommandIndex(runCommands, "npm --prefix panel run check:api");
        var panelTestIndex = FindCommandIndex(runCommands, "npm --prefix panel test");
        var panelBuildIndex = FindCommandIndex(runCommands, "npm --prefix panel run build");
        Assert.True(panelCiIndex >= 0, "Falta 'npm --prefix panel ci' en el job 'verify'.");
        Assert.True(panelCheckApiIndex > panelCiIndex, "Falta 'npm --prefix panel run check:api' tras 'npm --prefix panel ci'.");
        Assert.True(panelTestIndex > panelCiIndex, "Falta 'npm --prefix panel test' tras 'npm --prefix panel ci'.");
        Assert.True(panelBuildIndex > panelCiIndex, "Falta 'npm --prefix panel run build' tras 'npm --prefix panel ci'.");

        var restoreIndex = FindCommandIndex(runCommands, "dotnet restore CodeNetix.slnx");
        var buildIndex = runCommands.FindIndex(c =>
            c.Contains("dotnet build", StringComparison.Ordinal)
            && c.Contains("CodeNetix.slnx", StringComparison.Ordinal)
            && c.Contains("-c Release", StringComparison.Ordinal));
        var archTestsIndex = FindCommandIndex(runCommands, "CodeNetix.ArchitectureTests");
        var sandboxDockerIndex = runCommands.FindIndex(c =>
            c.Contains("docker build -f docker/sandbox.Dockerfile", StringComparison.Ordinal)
            && c.Contains("-t codenetix-sandbox:latest", StringComparison.Ordinal));
        var hostTestsIndex = runCommands.FindIndex(c =>
            c.Contains("CodeNetix.Host.Tests", StringComparison.Ordinal)
            && !c.Contains("--filter-trait Category=SecurityFire", StringComparison.Ordinal));
        var securityFireIndex = FindCommandIndex(runCommands, "--filter-trait Category=SecurityFire");

        Assert.True(restoreIndex >= 0, "Falta 'dotnet restore CodeNetix.slnx' en el job 'verify'.");
        Assert.True(buildIndex > restoreIndex, "Falta 'dotnet build CodeNetix.slnx -c Release' tras 'dotnet restore'.");
        Assert.True(archTestsIndex > buildIndex, "Falta 'CodeNetix.ArchitectureTests' tras 'dotnet build'.");
        Assert.True(sandboxDockerIndex >= 0, "Falta 'docker build -f docker/sandbox.Dockerfile -t codenetix-sandbox:latest'.");
        Assert.True(hostTestsIndex > buildIndex, "'CodeNetix.Host.Tests' debe ejecutarse tras 'dotnet build'.");
        Assert.True(hostTestsIndex > sandboxDockerIndex, "'CodeNetix.Host.Tests' debe ejecutarse tras construir la imagen sandbox.");
        Assert.True(securityFireIndex > buildIndex, "'Category=SecurityFire' debe ejecutarse tras 'dotnet build'.");
        Assert.True(securityFireIndex > sandboxDockerIndex, "'Category=SecurityFire' debe ejecutarse tras construir la imagen sandbox.");
        Assert.NotEqual(hostTestsIndex, securityFireIndex);

        Assert.Contains("CodeNetix.Host.Tests.csproj", runCommands[securityFireIndex], StringComparison.Ordinal);
        Assert.Contains("--filter-not-trait Category=SecurityFire", runCommands[hostTestsIndex], StringComparison.Ordinal);
        Assert.Contains("--fail-skips on", runCommands[hostTestsIndex], StringComparison.Ordinal);
        Assert.Contains("--fail-skips on", runCommands[securityFireIndex], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_images_job_publishes_multiarch_images_with_semver_tags_sbom_provenance_and_cosign_signing()
    {
        var workflowPath = FindRepoFile(Path.Combine(".github", "workflows", "release.yml"));
        var root = ParseYamlMapping(await File.ReadAllTextAsync(workflowPath, Ct));
        var jobs = GetRequiredMapping(root, "jobs");
        var publishJob = GetRequiredMapping(jobs, "publish-images");
        var steps = GetRequiredSequence(publishJob, "steps").Children.OfType<YamlMappingNode>().ToList();

        Assert.Contains(
            steps,
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("actions/checkout@", StringComparison.Ordinal));

        var qemuIndex = steps.FindIndex(
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("docker/setup-qemu-action@", StringComparison.Ordinal));
        var buildxIndex = steps.FindIndex(
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("docker/setup-buildx-action@", StringComparison.Ordinal));
        var loginIndex = steps.FindIndex(
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("docker/login-action@", StringComparison.Ordinal));
        var cosignInstallIndex = steps.FindIndex(
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("sigstore/cosign-installer@", StringComparison.Ordinal));

        Assert.True(qemuIndex >= 0, "Falta 'docker/setup-qemu-action' en 'publish-images'.");
        Assert.True(buildxIndex >= 0, "Falta 'docker/setup-buildx-action' en 'publish-images'.");
        Assert.True(loginIndex >= 0, "Falta 'docker/login-action' en 'publish-images'.");
        Assert.True(cosignInstallIndex >= 0, "Falta 'sigstore/cosign-installer' en 'publish-images'.");

        var loginWith = GetRequiredMapping(steps[loginIndex], "with");
        Assert.Equal("ghcr.io", GetRequiredScalar(loginWith, "registry"));
        Assert.Equal("${{ github.actor }}", GetRequiredScalar(loginWith, "username"));
        Assert.Equal("${{ secrets.GITHUB_TOKEN }}", GetRequiredScalar(loginWith, "password"));

        var metadataCount = steps.Count(
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("docker/metadata-action@", StringComparison.Ordinal));
        var buildCount = steps.Count(
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("docker/build-push-action@", StringComparison.Ordinal));
        var signCount = steps.Count(
            s => TryGetScalar(s, "run", out var run) && run is not null && run.Contains("cosign sign", StringComparison.Ordinal));
        Assert.Equal(2, metadataCount);
        Assert.Equal(2, buildCount);
        Assert.Equal(2, signCount);

        var codenetixSignIndex = AssertImagePublishAndSignChain(
            steps,
            qemuIndex,
            buildxIndex,
            loginIndex,
            cosignInstallIndex,
            metaId: "meta-codenetix",
            buildId: "build-codenetix",
            imageName: "ghcr.io/n3rvs/codenetix",
            dockerfile: "docker/codenetix.Dockerfile",
            cacheScope: "codenetix",
            digestEnvVar: "CODENETIX_DIGEST");

        var sandboxSignIndex = AssertImagePublishAndSignChain(
            steps,
            qemuIndex,
            buildxIndex,
            loginIndex,
            cosignInstallIndex,
            metaId: "meta-sandbox",
            buildId: "build-sandbox",
            imageName: "ghcr.io/n3rvs/codenetix-sandbox",
            dockerfile: "docker/sandbox.Dockerfile",
            cacheScope: "codenetix-sandbox",
            digestEnvVar: "SANDBOX_DIGEST");

        var sandboxBuildIndex = FindStepIndexById(steps, "build-sandbox");
        Assert.True(
            codenetixSignIndex < sandboxBuildIndex,
            "La firma de 'codenetix' debe completarse antes de publicar 'codenetix-sandbox' para no dejar imágenes publicadas sin firmar.");
        Assert.True(sandboxSignIndex > sandboxBuildIndex);
    }

    [Fact]
    public async Task Renovate_config_tracks_dockerfiles_ecosystem_managers_and_claude_cli_version_in_sandbox_contracts()
    {
        var renovatePath = FindRepoFile("renovate.json");
        var renovateJson = await File.ReadAllTextAsync(renovatePath, Ct);
        using var doc = JsonDocument.Parse(renovateJson);
        var root = doc.RootElement;

        Assert.Equal("https://docs.renovatebot.com/renovate-schema.json", root.GetProperty("$schema").GetString());
        var extends = root.GetProperty("extends").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("config:recommended", extends, StringComparer.Ordinal);

        var dockerfileManager = root.GetProperty("dockerfile");
        Assert.True(dockerfileManager.GetProperty("enabled").GetBoolean());
        Assert.False(dockerfileManager.TryGetProperty("fileMatch", out _), "renovate.json debe usar 'managerFilePatterns' en lugar de 'fileMatch' (deprecado).");
        var dockerfilePatterns = ReadRenovateRegexPatterns(dockerfileManager, "managerFilePatterns");

        Assert.Contains(dockerfilePatterns, p => Regex.IsMatch("docker/codenetix.Dockerfile", p, RegexOptions.CultureInvariant));
        Assert.Contains(dockerfilePatterns, p => Regex.IsMatch("docker/sandbox.Dockerfile", p, RegexOptions.CultureInvariant));
        Assert.DoesNotContain(dockerfilePatterns, p => Regex.IsMatch("README.md", p, RegexOptions.CultureInvariant));
        Assert.DoesNotContain(dockerfilePatterns, p => Regex.IsMatch("src/CodeNetix.Host/Program.cs", p, RegexOptions.CultureInvariant));

        var repoRoot = Path.GetDirectoryName(renovatePath)!;
        var dockerDir = Path.Combine(repoRoot, "docker");
        Assert.True(Directory.Exists(dockerDir), "Debe existir el directorio 'docker/' en el repositorio.");
        var dockerfiles = Directory.GetFiles(dockerDir, "*.Dockerfile", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(dockerfiles);
        foreach (var dockerfilePath in dockerfiles)
        {
            var relativePath = Path.GetRelativePath(repoRoot, dockerfilePath).Replace('\\', '/');
            Assert.True(
                dockerfilePatterns.Any(p => Regex.IsMatch(relativePath, p, RegexOptions.CultureInvariant)),
                $"renovate.json 'dockerfile.managerFilePatterns' debe cubrir '{relativePath}'.");
        }

        Assert.True(root.GetProperty("nuget").GetProperty("enabled").GetBoolean());
        Assert.True(root.GetProperty("github-actions").GetProperty("enabled").GetBoolean());

        var npmManager = root.GetProperty("npm");
        Assert.True(npmManager.GetProperty("enabled").GetBoolean());
        Assert.False(npmManager.TryGetProperty("fileMatch", out _), "renovate.json debe usar 'managerFilePatterns' en 'npm'.");
        var npmPatterns = ReadRenovateRegexPatterns(npmManager, "managerFilePatterns");
        Assert.Contains(npmPatterns, p => Regex.IsMatch("panel/package.json", p, RegexOptions.CultureInvariant));
        Assert.DoesNotContain(npmPatterns, p => Regex.IsMatch("README.md", p, RegexOptions.CultureInvariant));
        Assert.DoesNotContain(npmPatterns, p => Regex.IsMatch("src/CodeNetix.Host/Program.cs", p, RegexOptions.CultureInvariant));

        var customManagers = root.GetProperty("customManagers").EnumerateArray().ToList();
        var claudeManager = Assert.Single(
            customManagers,
            m => string.Equals(m.GetProperty("customType").GetString(), "regex", StringComparison.Ordinal)
                && string.Equals(m.GetProperty("depNameTemplate").GetString(), "@anthropic-ai/claude-code", StringComparison.Ordinal));

        Assert.Equal("npm", claudeManager.GetProperty("datasourceTemplate").GetString());
        Assert.False(claudeManager.TryGetProperty("fileMatch", out _), "renovate.json debe usar 'managerFilePatterns' en 'customManagers'.");

        var customFilePatterns = ReadRenovateRegexPatterns(claudeManager, "managerFilePatterns");
        Assert.Contains(customFilePatterns, p => Regex.IsMatch("src/CodeNetix.Contracts/Sandbox.cs", p, RegexOptions.CultureInvariant));
        Assert.DoesNotContain(customFilePatterns, p => Regex.IsMatch("README.md", p, RegexOptions.CultureInvariant));
        Assert.DoesNotContain(customFilePatterns, p => Regex.IsMatch("src/CodeNetix.Host/Program.cs", p, RegexOptions.CultureInvariant));

        var matchStrings = claudeManager.GetProperty("matchStrings")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        Assert.Single(matchStrings);

        var sandboxContractPath = FindRepoFile(Path.Combine("src", "CodeNetix.Contracts", "Sandbox.cs"));
        var sandboxContractSource = await File.ReadAllTextAsync(sandboxContractPath, Ct);

        var matches = Regex.Matches(sandboxContractSource, matchStrings[0], RegexOptions.CultureInvariant);
        var match = Assert.Single(matches);
        Assert.Equal(ISandboxToolsProvisioner.ClaudeCliVersion, match.Groups["currentValue"].Value);
    }

    [Fact]
    public async Task Codenetix_dockerfile_and_dockerignore_include_release_workflow_and_renovate_before_dotnet_test()
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

        var copyReleaseIndex = buildStageLines.FindIndex(l =>
            string.Equals(l, "COPY .github/workflows/release.yml .github/workflows/release.yml", StringComparison.Ordinal));
        var copyRenovateIndex = buildStageLines.FindIndex(l =>
            string.Equals(l, "COPY renovate.json renovate.json", StringComparison.Ordinal));
        var dotnetTestIndex = buildStageLines.FindIndex(l =>
            l.Contains("dotnet test", StringComparison.Ordinal));

        Assert.True(copyReleaseIndex >= 0, "La etapa 'AS build' debe copiar '.github/workflows/release.yml'.");
        Assert.True(copyRenovateIndex >= 0, "La etapa 'AS build' debe copiar 'renovate.json'.");
        Assert.True(dotnetTestIndex > copyReleaseIndex, "La etapa 'AS build' debe copiar '.github/workflows/release.yml' antes de ejecutar 'dotnet test'.");
        Assert.True(dotnetTestIndex > copyRenovateIndex, "La etapa 'AS build' debe copiar 'renovate.json' antes de ejecutar 'dotnet test'.");

        if (TryFindRepoFile(".dockerignore", out var dockerIgnorePath))
        {
            var ignoreLines = (await File.ReadAllLinesAsync(dockerIgnorePath!, Ct))
                .Select(l => l.Trim())
                .ToList();

            var githubWildcardIndex = ignoreLines.FindIndex(l => string.Equals(l, ".github/*", StringComparison.Ordinal));
            var workflowsUnignoreIndex = ignoreLines.FindIndex(l => string.Equals(l, "!.github/workflows/", StringComparison.Ordinal));
            var workflowsWildcardIndex = ignoreLines.FindIndex(l => string.Equals(l, ".github/workflows/*", StringComparison.Ordinal));
            var releaseUnignoreIndex = ignoreLines.FindIndex(l => string.Equals(l, "!.github/workflows/release.yml", StringComparison.Ordinal));

            Assert.True(githubWildcardIndex >= 0, ".dockerignore debe contener '.github/*'.");
            Assert.True(workflowsUnignoreIndex > githubWildcardIndex, "'!.github/workflows/' debe aparecer después de '.github/*'.");
            Assert.True(workflowsWildcardIndex > workflowsUnignoreIndex, "'.github/workflows/*' debe aparecer después de '!.github/workflows/'.");
            Assert.True(releaseUnignoreIndex > workflowsWildcardIndex, "'!.github/workflows/release.yml' debe aparecer después de '.github/workflows/*'.");
            Assert.DoesNotContain("renovate.json", ignoreLines, StringComparer.Ordinal);
        }
    }

    private static int AssertImagePublishAndSignChain(
        List<YamlMappingNode> steps,
        int qemuIndex,
        int buildxIndex,
        int loginIndex,
        int cosignInstallIndex,
        string metaId,
        string buildId,
        string imageName,
        string dockerfile,
        string cacheScope,
        string digestEnvVar)
    {
        var metaIndex = FindStepIndexById(steps, metaId);
        var buildIndex = FindStepIndexById(steps, buildId);
        var signIndex = steps.FindIndex(
            s => TryGetMapping(s, "env", out var env)
                && env is not null
                && TryGetScalar(env, digestEnvVar, out _));

        Assert.True(metaIndex >= 0, $"Falta el paso metadata '{metaId}'.");
        Assert.True(buildIndex >= 0, $"Falta el paso build-push '{buildId}'.");
        Assert.True(signIndex >= 0, $"Falta el paso de firma con variable '{digestEnvVar}'.");
        Assert.True(metaIndex < buildIndex, $"'{metaId}' debe ejecutarse antes de '{buildId}'.");
        Assert.True(buildIndex < signIndex, $"'{buildId}' debe ejecutarse antes de firmar '{imageName}'.");
        Assert.True(qemuIndex < buildIndex, $"'docker/setup-qemu-action' debe ir antes de '{buildId}'.");
        Assert.True(buildxIndex < buildIndex, $"'docker/setup-buildx-action' debe ir antes de '{buildId}'.");
        Assert.True(loginIndex < buildIndex, $"'docker/login-action' debe ir antes de '{buildId}'.");
        Assert.True(cosignInstallIndex < signIndex, $"'sigstore/cosign-installer' debe ir antes de firmar '{imageName}'.");

        var metaStep = steps[metaIndex];
        Assert.True(
            TryGetScalar(metaStep, "uses", out var metaUses) && metaUses is not null && metaUses.StartsWith("docker/metadata-action@", StringComparison.Ordinal),
            $"El paso '{metaId}' debe usar 'docker/metadata-action'.");
        var metaWith = GetRequiredMapping(metaStep, "with");
        Assert.Equal(imageName, GetRequiredScalar(metaWith, "images").Trim());
        var flavor = GetRequiredScalar(metaWith, "flavor");
        Assert.Contains("latest=false", flavor, StringComparison.Ordinal);
        Assert.DoesNotContain("latest=true", flavor, StringComparison.Ordinal);

        var tagRules = GetRequiredScalar(metaWith, "tags")
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, tagRules.Length);
        Assert.Contains("type=semver,pattern=v{{version}}", tagRules, StringComparer.Ordinal);
        Assert.Contains("type=semver,pattern=v{{major}}.{{minor}}", tagRules, StringComparer.Ordinal);
        Assert.DoesNotContain(tagRules, r => r.Contains("latest", StringComparison.OrdinalIgnoreCase));

        var buildStep = steps[buildIndex];
        Assert.True(
            TryGetScalar(buildStep, "uses", out var buildUses) && buildUses is not null && buildUses.StartsWith("docker/build-push-action@", StringComparison.Ordinal),
            $"El paso '{buildId}' debe usar 'docker/build-push-action'.");
        var buildWith = GetRequiredMapping(buildStep, "with");
        Assert.Equal(".", GetRequiredScalar(buildWith, "context"));
        Assert.Equal(dockerfile, GetRequiredScalar(buildWith, "file"));

        var platforms = GetRequiredScalar(buildWith, "platforms")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, platforms.Length);
        Assert.Contains("linux/amd64", platforms, StringComparer.Ordinal);
        Assert.Contains("linux/arm64", platforms, StringComparer.Ordinal);

        Assert.Equal("true", GetRequiredScalar(buildWith, "push"));
        Assert.Equal("true", GetRequiredScalar(buildWith, "sbom"));
        Assert.Equal("mode=max", GetRequiredScalar(buildWith, "provenance"));
        Assert.Equal($"${{{{ steps.{metaId}.outputs.tags }}}}", GetRequiredScalar(buildWith, "tags"));
        Assert.Equal($"${{{{ steps.{metaId}.outputs.labels }}}}", GetRequiredScalar(buildWith, "labels"));
        Assert.Contains($"type=gha,scope={cacheScope}", GetRequiredScalar(buildWith, "cache-from"), StringComparison.Ordinal);
        Assert.Contains($"scope={cacheScope}", GetRequiredScalar(buildWith, "cache-to"), StringComparison.Ordinal);
        Assert.Contains("type=gha", GetRequiredScalar(buildWith, "cache-to"), StringComparison.Ordinal);

        var signStep = steps[signIndex];
        var signEnv = GetRequiredMapping(signStep, "env");
        Assert.Single(signEnv.Children);
        Assert.Equal($"${{{{ steps.{buildId}.outputs.digest }}}}", GetRequiredScalar(signEnv, digestEnvVar));

        var signRun = GetRequiredScalar(signStep, "run");
        Assert.DoesNotContain("${{", signRun, StringComparison.Ordinal);
        Assert.Contains("set -euo pipefail", signRun, StringComparison.Ordinal);
        Assert.Contains("^sha256:[a-f0-9]{64}$", signRun, StringComparison.Ordinal);
        Assert.Contains($"cosign sign --yes --recursive \"{imageName}@${{{digestEnvVar}}}\"", signRun, StringComparison.Ordinal);

        return signIndex;
    }

    private static int FindStepIndexById(List<YamlMappingNode> steps, string id)
        => steps.FindIndex(s => TryGetScalar(s, "id", out var stepId) && string.Equals(stepId, id, StringComparison.Ordinal));

    private static string[] ReadRenovateRegexPatterns(JsonElement manager, string propertyName)
    {
        return manager.GetProperty(propertyName)
            .EnumerateArray()
            .Select(e => UnwrapRenovateRegex(e.GetString()!))
            .ToArray();
    }

    private static string UnwrapRenovateRegex(string pattern)
    {
        Assert.True(
            pattern.Length >= 2 && pattern[0] == '/' && pattern[^1] == '/',
            $"El patrón Renovate '{pattern}' debe usar delimitadores '/.../' en managerFilePatterns.");
        return pattern[1..^1];
    }

    private static void AssertNoWritePermissions(YamlNode permissionsNode, string scopeDescription)
    {
        if (permissionsNode is YamlScalarNode scalar)
        {
            Assert.False(
                string.Equals(scalar.Value, "write-all", StringComparison.OrdinalIgnoreCase),
                $"El {scopeDescription} no puede declarar 'permissions: write-all'.");
            return;
        }

        var mapping = Assert.IsType<YamlMappingNode>(permissionsNode);
        foreach (var entry in mapping.Children)
        {
            var value = Assert.IsType<YamlScalarNode>(entry.Value).Value;
            Assert.False(
                string.Equals(value, "write", StringComparison.OrdinalIgnoreCase),
                $"El {scopeDescription} no puede declarar permisos de escritura ('{entry.Key}: {value}').");
        }
    }

    private static int FindCommandIndex(List<string> commands, string expectedSubstring)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            if (commands[i].Contains(expectedSubstring, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static YamlMappingNode ParseYamlMapping(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        Assert.NotEmpty(stream.Documents);
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static bool TryGetNode(YamlMappingNode mapping, string key, out YamlNode? node)
    {
        foreach (var entry in mapping.Children)
        {
            if (entry.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                node = entry.Value;
                return true;
            }
        }

        node = null;
        return false;
    }

    private static bool TryGetMapping(YamlMappingNode mapping, string key, out YamlMappingNode? result)
    {
        if (TryGetNode(mapping, key, out var node) && node is YamlMappingNode map)
        {
            result = map;
            return true;
        }

        result = null;
        return false;
    }

    private static bool TryGetScalar(YamlMappingNode mapping, string key, out string? value)
    {
        if (TryGetNode(mapping, key, out var node) && node is YamlScalarNode scalar)
        {
            value = scalar.Value;
            return true;
        }

        value = null;
        return false;
    }

    private static YamlMappingNode GetRequiredMapping(YamlMappingNode mapping, string key)
    {
        Assert.True(TryGetMapping(mapping, key, out var result), $"Se esperaba un objeto YAML en la clave '{key}'.");
        return result!;
    }

    private static YamlSequenceNode GetRequiredSequence(YamlMappingNode mapping, string key)
    {
        Assert.True(TryGetNode(mapping, key, out var node) && node is YamlSequenceNode seq, $"Se esperaba una lista YAML en la clave '{key}'.");
        return (YamlSequenceNode)node!;
    }

    private static string GetRequiredScalar(YamlMappingNode mapping, string key)
    {
        Assert.True(TryGetScalar(mapping, key, out var value) && value is not null, $"Se esperaba un escalar YAML en la clave '{key}'.");
        return value!;
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
