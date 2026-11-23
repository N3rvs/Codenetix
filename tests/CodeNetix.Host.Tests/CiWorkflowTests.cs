using YamlDotNet.RepresentationModel;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas estructurales sobre el workflow de integración continua (<c>.github/workflows/ci.yml</c>)
/// y su disponibilidad dentro de <c>docker/codenetix.Dockerfile</c> (Historia 6.3).
/// </summary>
public sealed class CiWorkflowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ci_workflow_uses_safe_triggers_minimum_permissions_concurrency_and_bounded_ubuntu_jobs()
    {
        var workflowPath = FindRepoFile(Path.Combine(".github", "workflows", "ci.yml"));
        var root = ParseYamlMapping(await File.ReadAllTextAsync(workflowPath, Ct));

        Assert.Equal("CI", GetRequiredScalar(root, "name"));

        var onNode = GetRequiredMapping(root, "on");
        Assert.True(TryGetMapping(onNode, "push", out var pushNode), "El workflow debe dispararse en 'push'.");
        var pushBranches = GetRequiredSequence(pushNode!, "branches")
            .Children
            .OfType<YamlScalarNode>()
            .Select(n => n.Value)
            .ToArray();
        Assert.Contains("main", pushBranches, StringComparer.Ordinal);
        Assert.True(TryGetNode(onNode, "pull_request", out _), "El workflow debe dispararse en 'pull_request'.");
        Assert.False(TryGetNode(onNode, "pull_request_target", out _), "El workflow de CI nunca debe usar 'pull_request_target'.");
        Assert.False(TryGetNode(onNode, "workflow_run", out _), "El workflow de CI nunca debe usar 'workflow_run'.");

        var permissions = GetRequiredMapping(root, "permissions");
        Assert.Single(permissions.Children);
        Assert.Equal("read", GetRequiredScalar(permissions, "contents"));
        AssertNoWritePermissions(permissions, "workflow");

        var concurrency = GetRequiredMapping(root, "concurrency");
        Assert.False(string.IsNullOrWhiteSpace(GetRequiredScalar(concurrency, "group")));
        Assert.Equal("true", GetRequiredScalar(concurrency, "cancel-in-progress"));

        var jobs = GetRequiredMapping(root, "jobs");
        Assert.NotEmpty(jobs.Children);
        foreach (var entry in jobs.Children)
        {
            var jobName = Assert.IsType<YamlScalarNode>(entry.Key).Value!;
            var job = Assert.IsType<YamlMappingNode>(entry.Value);
            Assert.Equal("ubuntu-latest", GetRequiredScalar(job, "runs-on"));
            Assert.True(
                int.TryParse(GetRequiredScalar(job, "timeout-minutes"), out var timeout) && timeout > 0,
                $"El job '{jobName}' debe declarar un 'timeout-minutes' positivo.");

            if (TryGetNode(job, "permissions", out var jobPermissionsNode) && jobPermissionsNode is not null)
            {
                AssertNoWritePermissions(jobPermissionsNode, $"job '{jobName}'");
            }
        }
    }

    [Fact]
    public async Task Panel_job_validates_openapi_tests_and_production_build_on_node_24()
    {
        var workflowPath = FindRepoFile(Path.Combine(".github", "workflows", "ci.yml"));
        var root = ParseYamlMapping(await File.ReadAllTextAsync(workflowPath, Ct));
        var jobs = GetRequiredMapping(root, "jobs");
        var panelJob = GetRequiredMapping(jobs, "panel");
        var steps = GetRequiredSequence(panelJob, "steps").Children.OfType<YamlMappingNode>().ToList();

        Assert.Contains(
            steps,
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("actions/checkout@", StringComparison.Ordinal));

        var setupNodeStep = Assert.Single(
            steps,
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("actions/setup-node@", StringComparison.Ordinal));
        var setupNodeWith = GetRequiredMapping(setupNodeStep, "with");
        Assert.Equal("24", GetRequiredScalar(setupNodeWith, "node-version"));
        Assert.Equal("npm", GetRequiredScalar(setupNodeWith, "cache"));
        Assert.Equal("panel/package-lock.json", GetRequiredScalar(setupNodeWith, "cache-dependency-path"));

        var runCommands = steps
            .Select(s => TryGetScalar(s, "run", out var run) ? run : null)
            .Where(run => run is not null)
            .Cast<string>()
            .ToList();

        var ciIndex = FindCommandIndex(runCommands, "npm --prefix panel ci");
        var checkApiIndex = FindCommandIndex(runCommands, "npm --prefix panel run check:api");
        var testIndex = FindCommandIndex(runCommands, "npm --prefix panel test");
        var buildIndex = FindCommandIndex(runCommands, "npm --prefix panel run build");

        Assert.True(ciIndex >= 0, "Falta el paso 'npm --prefix panel ci' en el job 'panel'.");
        Assert.True(checkApiIndex > ciIndex, "Falta 'npm --prefix panel run check:api' tras 'npm --prefix panel ci'.");
        Assert.True(testIndex > ciIndex, "Falta 'npm --prefix panel test' tras 'npm --prefix panel ci'.");
        Assert.True(buildIndex > ciIndex, "Falta 'npm --prefix panel run build' tras 'npm --prefix panel ci'.");
    }

    [Fact]
    public async Task Build_and_test_job_runs_dotnet_build_architecture_tests_sandbox_docker_host_tests_and_security_fire()
    {
        var workflowPath = FindRepoFile(Path.Combine(".github", "workflows", "ci.yml"));
        var root = ParseYamlMapping(await File.ReadAllTextAsync(workflowPath, Ct));
        var jobs = GetRequiredMapping(root, "jobs");
        var buildJob = GetRequiredMapping(jobs, "build-and-test");
        var steps = GetRequiredSequence(buildJob, "steps").Children.OfType<YamlMappingNode>().ToList();

        Assert.Contains(
            steps,
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("actions/checkout@", StringComparison.Ordinal));

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

        Assert.True(restoreIndex >= 0, "Falta el paso 'dotnet restore CodeNetix.slnx' en el job 'build-and-test'.");
        Assert.True(buildIndex > restoreIndex, "Falta el paso 'dotnet build CodeNetix.slnx -c Release' tras 'dotnet restore'.");
        Assert.True(archTestsIndex > buildIndex, "Falta el paso de 'CodeNetix.ArchitectureTests' tras 'dotnet build'.");
        Assert.True(sandboxDockerIndex >= 0, "Falta 'docker build -f docker/sandbox.Dockerfile -t codenetix-sandbox:latest' en el job 'build-and-test'.");
        Assert.True(hostTestsIndex >= 0, "Falta el paso general de 'CodeNetix.Host.Tests' en el job 'build-and-test'.");
        Assert.True(securityFireIndex >= 0, "Falta el paso dedicado '--filter-trait Category=SecurityFire' en el job 'build-and-test'.");
        Assert.NotEqual(hostTestsIndex, securityFireIndex);
        Assert.True(hostTestsIndex > buildIndex, "'CodeNetix.Host.Tests' debe ejecutarse después de 'dotnet build'.");
        Assert.True(hostTestsIndex > sandboxDockerIndex, "'CodeNetix.Host.Tests' debe ejecutarse después de construir 'docker/sandbox.Dockerfile'.");
        Assert.True(securityFireIndex > buildIndex, "La prueba de fuego 'Category=SecurityFire' debe ejecutarse después de 'dotnet build'.");
        Assert.True(securityFireIndex > sandboxDockerIndex, "La prueba de fuego 'Category=SecurityFire' debe ejecutarse después de construir 'docker/sandbox.Dockerfile'.");

        Assert.Contains("--filter-not-trait Category=SecurityFire", runCommands[hostTestsIndex], StringComparison.Ordinal);
        Assert.Contains("--fail-skips on", runCommands[hostTestsIndex], StringComparison.Ordinal);
        Assert.Contains("--fail-skips on", runCommands[securityFireIndex], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docker_multiarch_job_builds_both_images_for_amd64_and_arm64_without_pushing()
    {
        var workflowPath = FindRepoFile(Path.Combine(".github", "workflows", "ci.yml"));
        var root = ParseYamlMapping(await File.ReadAllTextAsync(workflowPath, Ct));
        var jobs = GetRequiredMapping(root, "jobs");
        var dockerJob = GetRequiredMapping(jobs, "docker-multiarch");

        var needs = GetRequiredSequence(dockerJob, "needs")
            .Children
            .OfType<YamlScalarNode>()
            .Select(n => n.Value)
            .ToArray();
        Assert.Contains("panel", needs, StringComparer.Ordinal);
        Assert.Contains("build-and-test", needs, StringComparer.Ordinal);

        var steps = GetRequiredSequence(dockerJob, "steps").Children.OfType<YamlMappingNode>().ToList();
        Assert.Contains(
            steps,
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("actions/checkout@", StringComparison.Ordinal));

        var qemuIndex = steps.FindIndex(
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("docker/setup-qemu-action@", StringComparison.Ordinal));
        var buildxIndex = steps.FindIndex(
            s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("docker/setup-buildx-action@", StringComparison.Ordinal));
        Assert.True(qemuIndex >= 0, "Falta 'docker/setup-qemu-action' en el job 'docker-multiarch'.");
        Assert.True(buildxIndex >= 0, "Falta 'docker/setup-buildx-action' en el job 'docker-multiarch'.");

        var buildSteps = steps
            .Select((step, index) => (Step: step, Index: index))
            .Where(item => TryGetScalar(item.Step, "uses", out var uses) && uses is not null && uses.StartsWith("docker/build-push-action@", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, buildSteps.Count);

        var builtFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (step, buildStepIndex) in buildSteps)
        {
            Assert.True(qemuIndex < buildStepIndex, "'docker/setup-qemu-action' debe ejecutarse antes de 'docker/build-push-action'.");
            Assert.True(buildxIndex < buildStepIndex, "'docker/setup-buildx-action' debe ejecutarse antes de 'docker/build-push-action'.");

            var withNode = GetRequiredMapping(step, "with");
            Assert.Equal(".", GetRequiredScalar(withNode, "context"));

            var file = GetRequiredScalar(withNode, "file");
            builtFiles.Add(file);

            var platforms = GetRequiredScalar(withNode, "platforms")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("linux/amd64", platforms, StringComparer.Ordinal);
            Assert.Contains("linux/arm64", platforms, StringComparer.Ordinal);
            Assert.Equal("false", GetRequiredScalar(withNode, "push"));
            Assert.Contains("type=gha", GetRequiredScalar(withNode, "cache-from"), StringComparison.Ordinal);
            Assert.Contains("type=gha", GetRequiredScalar(withNode, "cache-to"), StringComparison.Ordinal);
        }

        Assert.Contains("docker/codenetix.Dockerfile", builtFiles);
        Assert.Contains("docker/sandbox.Dockerfile", builtFiles);
    }

    [Fact]
    public async Task Codenetix_dockerfile_copies_ci_workflow_before_running_dotnet_test_and_uses_buildplatform()
    {
        var dockerfilePath = FindRepoFile(Path.Combine("docker", "codenetix.Dockerfile"));
        var content = await File.ReadAllTextAsync(dockerfilePath, Ct);

        Assert.Contains("FROM --platform=$BUILDPLATFORM node:24 AS panel", content, StringComparison.Ordinal);
        Assert.Contains("FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build", content, StringComparison.Ordinal);

        var copyCiIndex = content.IndexOf("COPY .github/workflows/ci.yml .github/workflows/ci.yml", StringComparison.Ordinal);
        var copyDockerfileIndex = content.IndexOf("COPY docker/codenetix.Dockerfile docker/codenetix.Dockerfile", StringComparison.Ordinal);
        var dotnetTestIndex = content.IndexOf("dotnet test", StringComparison.Ordinal);

        Assert.True(copyCiIndex >= 0, "docker/codenetix.Dockerfile debe copiar '.github/workflows/ci.yml' en la etapa build.");
        Assert.True(copyDockerfileIndex >= 0, "docker/codenetix.Dockerfile debe copiar 'docker/codenetix.Dockerfile' en la etapa build.");
        Assert.True(dotnetTestIndex > copyCiIndex, "docker/codenetix.Dockerfile debe copiar '.github/workflows/ci.yml' antes de ejecutar 'dotnet test'.");
        Assert.True(dotnetTestIndex > copyDockerfileIndex, "docker/codenetix.Dockerfile debe copiar 'docker/codenetix.Dockerfile' antes de ejecutar 'dotnet test'.");

        if (TryFindRepoFile(".dockerignore", out var dockerIgnorePath))
        {
            var lines = (await File.ReadAllLinesAsync(dockerIgnorePath!, Ct))
                .Select(l => l.Trim())
                .ToArray();
            Assert.DoesNotContain(".github/", lines, StringComparer.Ordinal);
            Assert.Contains("!.github/workflows/", lines, StringComparer.Ordinal);
            Assert.Contains("!.github/workflows/ci.yml", lines, StringComparer.Ordinal);
        }
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
