using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Pruebas de contrato y seguridad sobre la acción <c>composite</c> publicada (<c>action.yml</c>),
/// el workflow de ejemplo (<c>examples/workflows/codenetix.yml</c>) y la documentación en <c>README.md</c>
/// (Historia 6.2, FR-25).
/// </summary>
public sealed class CompositeActionTests
{
    private static readonly string[] ExpectedActionInputs =
    [
        "version",
        "github-token",
        "anthropic-api-key",
        "openai-api-key",
        "label",
        "budget-usd",
        "image",
        "sandbox-image"
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Action_yml_defines_composite_bash_action_with_semver_inputs_and_no_shell_injection()
    {
        var actionPath = FindRepoFile("action.yml");
        var root = ParseYamlMapping(await File.ReadAllTextAsync(actionPath, Ct));

        Assert.Equal("CodeNetix", GetRequiredScalar(root, "name"));
        Assert.False(string.IsNullOrWhiteSpace(GetRequiredScalar(root, "description")));

        var branding = GetRequiredMapping(root, "branding");
        Assert.Equal("git-pull-request", GetRequiredScalar(branding, "icon"));
        Assert.Equal("blue", GetRequiredScalar(branding, "color"));

        var inputs = GetRequiredMapping(root, "inputs");
        foreach (var inputName in ExpectedActionInputs)
        {
            Assert.True(TryGetMapping(inputs, inputName, out var inputMap), $"Falta el input '{inputName}' en action.yml.");
            Assert.False(
                string.IsNullOrWhiteSpace(GetRequiredScalar(inputMap!, "description")),
                $"El input '{inputName}' debe incluir una descripción no vacía.");
        }

        var versionInput = GetRequiredMapping(inputs, "version");
        var defaultVersion = GetRequiredScalar(versionInput, "default");
        Assert.Matches(@"^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", defaultVersion);
        Assert.DoesNotContain("latest", defaultVersion, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("${{ github.token }}", GetRequiredScalar(GetRequiredMapping(inputs, "github-token"), "default"));
        Assert.Equal("codenetix", GetRequiredScalar(GetRequiredMapping(inputs, "label"), "default"));

        var runs = GetRequiredMapping(root, "runs");
        Assert.Equal("composite", GetRequiredScalar(runs, "using"));

        var steps = GetRequiredSequence(runs, "steps");
        Assert.NotEmpty(steps.Children);

        foreach (var stepNode in steps.Children)
        {
            var step = Assert.IsType<YamlMappingNode>(stepNode);
            Assert.Equal("bash", GetRequiredScalar(step, "shell"));
            var runScript = GetRequiredScalar(step, "run");
            Assert.DoesNotContain("${{", runScript, StringComparison.Ordinal);
        }

        var firstStep = Assert.IsType<YamlMappingNode>(steps.Children[0]);
        var env = GetRequiredMapping(firstStep, "env");
        Assert.Contains("inputs.version", GetRequiredScalar(env, "INPUT_VERSION"), StringComparison.Ordinal);
        Assert.Contains("inputs.github-token", GetRequiredScalar(env, "GITHUB_TOKEN"), StringComparison.Ordinal);
        Assert.Contains("inputs.anthropic-api-key", GetRequiredScalar(env, "CODENETIX__Providers__Anthropic__ApiKey"), StringComparison.Ordinal);
        Assert.Contains("inputs.openai-api-key", GetRequiredScalar(env, "CODENETIX__Providers__OpenAI__ApiKey"), StringComparison.Ordinal);
        Assert.Contains("inputs.label", GetRequiredScalar(env, "CODENETIX__Action__Label"), StringComparison.Ordinal);
        Assert.Contains("inputs.budget-usd", GetRequiredScalar(env, "CODENETIX__Limits__BudgetUsd"), StringComparison.Ordinal);
        Assert.Contains("inputs.image", GetRequiredScalar(env, "INPUT_IMAGE"), StringComparison.Ordinal);
        Assert.Contains("inputs.sandbox-image", GetRequiredScalar(env, "INPUT_SANDBOX_IMAGE"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Action_yml_run_script_mounts_docker_socket_uses_no_healthcheck_and_keeps_secrets_out_of_argv()
    {
        var actionPath = FindRepoFile("action.yml");
        var root = ParseYamlMapping(await File.ReadAllTextAsync(actionPath, Ct));
        var steps = GetRequiredSequence(GetRequiredMapping(root, "runs"), "steps");
        var step = Assert.IsType<YamlMappingNode>(steps.Children[0]);
        var runScript = GetRequiredScalar(step, "run");

        Assert.Contains("[ ! -S /var/run/docker.sock ]", runScript, StringComparison.Ordinal);
        Assert.Contains("GITHUB_EVENT_PATH", runScript, StringComparison.Ordinal);
        Assert.Contains("validate_image_ref", runScript, StringComparison.Ordinal);
        Assert.Contains("*:latest", runScript, StringComparison.Ordinal);
        Assert.Contains("stat -L", runScript, StringComparison.Ordinal);
        Assert.Contains("export CODENETIX__Sandbox__Image", runScript, StringComparison.Ordinal);
        Assert.Contains("export CODENETIX__Sandbox__HostContainer", runScript, StringComparison.Ordinal);

        Assert.Contains("--rm", runScript, StringComparison.Ordinal);
        Assert.Contains("--no-healthcheck", runScript, StringComparison.Ordinal);
        Assert.Contains("--group-add", runScript, StringComparison.Ordinal);
        Assert.Contains("/var/run/docker.sock:/var/run/docker.sock", runScript, StringComparison.Ordinal);
        Assert.Contains("${EVENT_DIR}/event.json:/run/codenetix-event.json:ro", runScript, StringComparison.Ordinal);

        Assert.Contains("-e GITHUB_EVENT_NAME", runScript, StringComparison.Ordinal);
        Assert.Contains("-e GITHUB_EVENT_PATH=/run/codenetix-event.json", runScript, StringComparison.Ordinal);
        Assert.Contains("-e GITHUB_REPOSITORY", runScript, StringComparison.Ordinal);
        Assert.Contains("-e GITHUB_RUN_ID", runScript, StringComparison.Ordinal);
        Assert.Contains("-e GITHUB_RUN_ATTEMPT", runScript, StringComparison.Ordinal);
        Assert.Contains("-e GITHUB_ACTIONS", runScript, StringComparison.Ordinal);
        Assert.Contains("-e GITHUB_API_URL", runScript, StringComparison.Ordinal);
        Assert.Contains("-e GITHUB_SERVER_URL", runScript, StringComparison.Ordinal);
        Assert.Contains("-e CODENETIX__Action__Label", runScript, StringComparison.Ordinal);
        Assert.Contains("-e CODENETIX__Sandbox__Image", runScript, StringComparison.Ordinal);
        Assert.Contains("-e CODENETIX__Sandbox__HostContainer", runScript, StringComparison.Ordinal);
        Assert.Contains("-e CODENETIX__Limits__BudgetUsd", runScript, StringComparison.Ordinal);

        Assert.Contains("ghcr.io/n3rvs/codenetix:", runScript, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/n3rvs/codenetix-sandbox:", runScript, StringComparison.Ordinal);
        Assert.Contains("docker pull \"$CODENETIX_IMAGE\"", runScript, StringComparison.Ordinal);
        Assert.Contains("docker pull \"$CODENETIX__Sandbox__Image\"", runScript, StringComparison.Ordinal);
        Assert.Matches(@"docker run ""\$\{DOCKER_ARGS\[@\]\}"" ""\$CODENETIX_IMAGE"" action", runScript);

        Assert.Contains("trap ", runScript, StringComparison.Ordinal);
        Assert.Contains("SIGINT", runScript, StringComparison.Ordinal);
        Assert.Contains("SIGTERM", runScript, StringComparison.Ordinal);
        Assert.Contains("docker stop", runScript, StringComparison.Ordinal);

        // Los secretos deben heredarse del entorno con `-e VAR` y nunca pasarse con `=VALOR` en argv.
        Assert.Contains("-e GITHUB_TOKEN", runScript, StringComparison.Ordinal);
        Assert.Contains("-e CODENETIX__Providers__Anthropic__ApiKey", runScript, StringComparison.Ordinal);
        Assert.Contains("-e CODENETIX__Providers__OpenAI__ApiKey", runScript, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"(?:--env(?:=|\s+)|-e\s*)(?:GITHUB_TOKEN|CODENETIX__Providers__[A-Za-z0-9_]+)=", RegexOptions.CultureInvariant),
            runScript);
    }

    [Fact]
    public async Task Example_workflow_triggers_only_on_labeled_issues_with_required_permissions_and_semver_ref()
    {
        var actionRoot = ParseYamlMapping(await File.ReadAllTextAsync(FindRepoFile("action.yml"), Ct));
        var actionInputs = GetRequiredMapping(actionRoot, "inputs");
        var defaultVersion = GetRequiredScalar(GetRequiredMapping(actionInputs, "version"), "default");

        var workflowPath = FindRepoFile(Path.Combine("examples", "workflows", "codenetix.yml"));
        var content = await File.ReadAllTextAsync(workflowPath, Ct);
        var root = ParseYamlMapping(content);

        var onNode = GetRequiredMapping(root, "on");
        Assert.Single(onNode.Children);
        Assert.True(TryGetMapping(onNode, "issues", out var issuesNode), "El workflow debe dispararse solo con 'issues'.");
        Assert.False(TryGetNode(onNode, "pull_request_target", out _), "El workflow nunca debe usar 'pull_request_target'.");
        Assert.False(TryGetNode(onNode, "workflow_run", out _), "El workflow nunca debe usar 'workflow_run'.");

        var issueTypes = GetRequiredSequence(issuesNode!, "types");
        var singleType = Assert.Single(issueTypes.Children);
        Assert.Equal("labeled", Assert.IsType<YamlScalarNode>(singleType).Value);

        var permissions = GetRequiredMapping(root, "permissions");
        Assert.Equal("write", GetRequiredScalar(permissions, "contents"));
        Assert.Equal("write", GetRequiredScalar(permissions, "pull-requests"));
        Assert.Equal("write", GetRequiredScalar(permissions, "issues"));

        var jobs = GetRequiredMapping(root, "jobs");
        var job = GetRequiredMapping(jobs, "codenetix");
        Assert.Equal("ubuntu-latest", GetRequiredScalar(job, "runs-on"));
        Assert.True(int.TryParse(GetRequiredScalar(job, "timeout-minutes"), out var timeout) && timeout > 0);
        Assert.Equal("github.event.label.name == 'codenetix'", GetRequiredScalar(job, "if"));

        var steps = GetRequiredSequence(job, "steps");
        var actionStep = steps.Children
            .OfType<YamlMappingNode>()
            .Single(s => TryGetScalar(s, "uses", out var uses) && uses is not null && uses.StartsWith("n3rvs/codenetix@", StringComparison.Ordinal));

        Assert.Equal($"n3rvs/codenetix@{defaultVersion}", GetRequiredScalar(actionStep, "uses"));

        var withNode = GetRequiredMapping(actionStep, "with");
        foreach (var entry in withNode.Children)
        {
            var key = Assert.IsType<YamlScalarNode>(entry.Key).Value!;
            Assert.True(
                TryGetMapping(actionInputs, key, out _),
                $"El input '{key}' usado en examples/workflows/codenetix.yml no existe en action.yml.");
        }

        Assert.Equal("${{ secrets.GITHUB_TOKEN }}", GetRequiredScalar(withNode, "github-token"));
        Assert.Equal("${{ secrets.ANTHROPIC_API_KEY }}", GetRequiredScalar(withNode, "anthropic-api-key"));
        Assert.DoesNotContain("@latest", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":latest", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readme_documents_composite_action_permissions_and_security_warnings()
    {
        var actionRoot = ParseYamlMapping(await File.ReadAllTextAsync(FindRepoFile("action.yml"), Ct));
        var defaultVersion = GetRequiredScalar(
            GetRequiredMapping(GetRequiredMapping(actionRoot, "inputs"), "version"),
            "default");

        var readmePath = FindRepoFile("README.md");
        var readme = await File.ReadAllTextAsync(readmePath, Ct);

        Assert.Contains($"n3rvs/codenetix@{defaultVersion}", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("n3rvs/codenetix@latest", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("llega en la siguiente versión", readme, StringComparison.OrdinalIgnoreCase);

        var actionSectionMatch = Regex.Match(
            readme,
            @"## Modo GitHub Action.*?(?=\r?\n## |\z)",
            RegexOptions.Singleline);
        Assert.True(actionSectionMatch.Success, "Falta la sección '## Modo GitHub Action' en README.md.");
        var actionSection = actionSectionMatch.Value;

        Assert.DoesNotContain(":latest", actionSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@latest", actionSection, StringComparison.OrdinalIgnoreCase);

        foreach (var inputName in ExpectedActionInputs)
        {
            Assert.Contains($"`{inputName}`", actionSection, StringComparison.Ordinal);
        }

        Assert.Contains("contents: write", actionSection, StringComparison.Ordinal);
        Assert.Contains("pull-requests: write", actionSection, StringComparison.Ordinal);
        Assert.Contains("issues: write", actionSection, StringComparison.Ordinal);
        Assert.Contains("Allow GitHub Actions to create and approve pull requests", actionSection, StringComparison.Ordinal);

        Assert.Matches(
            new Regex(@"secrets.{0,80}no llegan a workflows disparados desde forks", RegexOptions.IgnoreCase | RegexOptions.Singleline),
            actionSection);
        Assert.Matches(
            new Regex(@"GITHUB_TOKEN.{0,120}no disparan", RegexOptions.IgnoreCase | RegexOptions.Singleline),
            actionSection);
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

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"No se encontró '{relativePath}' ascendiendo desde '{AppContext.BaseDirectory}'.");
    }
}
