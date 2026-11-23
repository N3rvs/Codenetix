using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;
using CodeNetix.Pipeline;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Features.ResolveConfig;

namespace CodeNetix.Host.Tests;

/// <summary>Tests de tabla de <c>RepoConfigValidator</c> (esquema <c>codenetix.v1.json</c>) y de <c>EffectiveConfigCalculator</c>.</summary>
public sealed class RepoConfigTests
{
    // --- Validador: archivos válidos ------------------------------------------------------

    [Fact]
    public void Full_valid_file_is_parsed()
    {
        var result = RepoConfigValidator.Validate("""
            version: 1
            protected_paths:
              - "src/Auth/**"
              - docs/**
            reviewers: [security, tests]
            max_review_rounds: 1
            max_coding_attempts: 2
            budget_usd: 2.5
            models:
              analyst: gpt-x
              coder: "claude-y"
              reviewers: local/qwen
            """);

        Assert.True(result.IsValid, result.Error);
        var config = result.Config!;
        Assert.Equal(1, config.Version);
        Assert.Equal(["src/Auth/**", "docs/**"], config.ProtectedPaths);
        Assert.Equal(["security", "tests"], config.Reviewers);
        Assert.Equal(1, config.MaxReviewRounds);
        Assert.Equal(2, config.MaxCodingAttempts);
        Assert.Equal(2.5m, config.BudgetUsd);
        Assert.Equal(new RepoConfigModels("gpt-x", "claude-y", "local/qwen"), config.Models);
    }

    [Theory]
    [InlineData("version: 1")]
    [InlineData("version: 1\n")]
    [InlineData("# comentario\nversion: 1\nreviewers: [quality]\n")]
    [InlineData("version: 1\nbudget_usd: 3\n")]
    [InlineData("version: 1\nmax_review_rounds: 5\nmax_coding_attempts: 5\n")]
    [InlineData("version: 1\nmodels: {}\n")]
    [InlineData("---\nversion: 1\n")]
    [InlineData("version: 1\r\nreviewers:\r\n  - security\r\n")]
    public void Minimal_and_edge_valid_files_pass(string yaml)
    {
        var result = RepoConfigValidator.Validate(yaml);

        Assert.True(result.IsValid, result.Error);
        Assert.Null(result.Error);
    }

    // --- Validador: YAML mal formado ------------------------------------------------------

    [Theory]
    [InlineData("version: 1\nreviewers:\n\t- security\n")]
    [InlineData("version: 1\nreviewers: [security\n")]
    [InlineData("version: 1\n  bad: indent\n")]
    [InlineData("version: 1\nversion: 1\n")]
    [InlineData("version: 1\n\"sin cerrar: 2\n")]
    public void Malformed_yaml_reports_line_and_column(string yaml)
    {
        var result = RepoConfigValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.StartsWith("codenetix.yml: YAML no válido en la línea ", result.Error, StringComparison.Ordinal);
        Assert.Matches(@"línea \d+, columna \d+: \S", result.Error!);
    }

    [Theory]
    [InlineData("version: 1\nreviewers:\n\t- security\n", "línea 3, columna 1: tabulador en la sangría (usa espacios)")]
    [InlineData("version: 1\nversion: 1\n", "línea 2, columna 1: clave repetida 'version'")]
    public void Common_yaml_errors_are_explained_in_spanish_at_their_position(string yaml, string expected)
    {
        var result = RepoConfigValidator.Validate(yaml);

        Assert.Equal("codenetix.yml: YAML no válido en la " + expected, result.Error);
    }

    // --- Validador: entradas hostiles o numéricas límite (revisión 1) ------------------------

    [Theory]
    [InlineData("version: 1.0\n")]
    [InlineData("version: 1\nmax_review_rounds: 2.0\nmax_coding_attempts: 3.00\n")]
    [InlineData("version: 1\nmax_review_rounds: 1e0\n")]
    public void Whole_decimals_are_accepted_as_integers(string yaml)
    {
        var result = RepoConfigValidator.Validate(yaml);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(1, result.Config!.Version);
    }

    [Theory]
    [InlineData("version: 1\nbudget_usd: 1e30\n", "número fuera de rango")]
    [InlineData("version: 1\nmax_review_rounds: 99999999999999999999999999999999\n", "número fuera de rango")]
    [InlineData("version: 1\nmax_review_rounds: 2.5\n", "$.max_review_rounds: tipo no válido")]
    [InlineData("a: &a [*a]\n", "alias '*a' sin ancla previa o recursivo")]
    [InlineData("a: &a {b: *a}\n", "alias '*a' sin ancla previa o recursivo")]
    [InlineData("version: 1\nx: *nada\n", "alias '*nada' sin ancla previa o recursivo")]
    [InlineData("{[a]: 1}\n", "las claves deben ser texto")]
    public void Hostile_or_out_of_range_values_are_InvalidRepoConfig(string yaml, string expected)
    {
        var result = RepoConfigValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.StartsWith("codenetix.yml: ", result.Error, StringComparison.Ordinal);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(31, true)]
    [InlineData(33, false)]
    [InlineData(100, false)]
    [InlineData(10_000, false)]
    public void Deep_nesting_is_rejected_without_crashing(int depth, bool withinLimit)
    {
        // version: 1 + x: [[[…]]] (flujo) y y: {a: {a: …}} (bloque no: el flujo basta para la profundidad).
        var flow = "version: 1\nx: " + new string('[', depth - 1) + new string(']', depth - 1) + "\n";
        var mapping = "version: 1\nx: " + string.Concat(Enumerable.Repeat("{a: ", depth - 1)) + "1" + new string('}', depth - 1) + "\n";

        foreach (var yaml in new[] { flow, mapping })
        {
            var result = RepoConfigValidator.Validate(yaml);

            Assert.False(result.IsValid); // x no es una clave admitida: solo importa que no reviente
            Assert.StartsWith("codenetix.yml: ", result.Error, StringComparison.Ordinal);
            if (withinLimit)
            {
                Assert.Contains("$.x: clave no admitida", result.Error, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("anidamiento demasiado profundo", result.Error, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Anchors_and_aliases_to_closed_nodes_still_work()
    {
        var result = RepoConfigValidator.Validate("version: 1\nreviewers: &r [security]\nprotected_paths: &p [\"a/**\"]\nmodels:\n  analyst: &m x\n  coder: *m\n");

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(new RepoConfigModels("x", "x", null), result.Config!.Models);
    }

    [Fact]
    public void Several_documents_are_rejected()
    {
        var result = RepoConfigValidator.Validate("version: 1\n---\nversion: 1\n");

        Assert.Equal("codenetix.yml: solo se admite un documento YAML", result.Error);
    }

    [Fact]
    public void Alias_expansion_bomb_is_rejected()
    {
        var yaml = "a: &a [x, x, x, x, x, x, x, x, x, x]\n"
            + "b: &b [*a, *a, *a, *a, *a, *a, *a, *a, *a, *a]\n"
            + "c: &c [*b, *b, *b, *b, *b, *b, *b, *b, *b, *b]\n"
            + "d: &d [*c, *c, *c, *c, *c, *c, *c, *c, *c, *c]\n"
            + "e: [*d, *d, *d, *d, *d, *d, *d, *d, *d, *d]\n";

        var result = RepoConfigValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.Contains("demasiado grande", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_file_is_rejected()
    {
        var result = RepoConfigValidator.Validate("version: 1\n" + new string('#', RepoConfigValidator.MaxLength));

        Assert.False(result.IsValid);
        Assert.StartsWith("codenetix.yml: el archivo supera", result.Error, StringComparison.Ordinal);
    }

    // --- Validador: esquema ---------------------------------------------------------------

    [Theory]
    [InlineData("version: 1\nreviewers: [hacker]\n", "$.reviewers[0]: valor no admitido")]
    [InlineData("version: 1\nreviewers: [security, security]\n", "$.reviewers: tiene elementos repetidos")]
    [InlineData("version: 1\nreviewers: []\n", "$.reviewers: no puede estar vacío")]
    [InlineData("version: 1\nhacker: true\n", "$.hacker: clave no admitida")]
    [InlineData("version: 1\nmodels:\n  planner: x\n", "$.models.planner: clave no admitida")]
    [InlineData("reviewers: [security]\n", "$: falta la clave obligatoria 'version'")]
    [InlineData("version: 2\n", "$.version: la versión debe ser 1")]
    [InlineData("version: \"1\"\n", "$.version: la versión debe ser 1")]
    [InlineData("version: 1\nmax_review_rounds: 6\n", "$.max_review_rounds: fuera de rango")]
    [InlineData("version: 1\nmax_review_rounds: 0\n", "$.max_review_rounds: fuera de rango")]
    [InlineData("version: 1\nmax_coding_attempts: 1.5\n", "$.max_coding_attempts: tipo no válido")]
    [InlineData("version: 1\nmax_coding_attempts: \"2\"\n", "$.max_coding_attempts: tipo no válido")]
    [InlineData("version: 1\nbudget_usd: 0\n", "$.budget_usd: debe ser mayor que 0")]
    [InlineData("version: 1\nbudget_usd: -3\n", "$.budget_usd: debe ser mayor que 0")]
    [InlineData("version: 1\nprotected_paths: src/**\n", "$.protected_paths: tipo no válido")]
    [InlineData("version: 1\nprotected_paths: [\"\"]\n", "$.protected_paths[0]: no puede estar vacío")]
    [InlineData("- version: 1\n", "$: tipo no válido")]
    [InlineData("", "$: tipo no válido")]
    public void Schema_violations_report_the_json_path(string yaml, string expected)
    {
        var result = RepoConfigValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.StartsWith("codenetix.yml: ", result.Error, StringComparison.Ordinal);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_schema_errors_are_joined_with_semicolons()
    {
        var result = RepoConfigValidator.Validate("version: 1\nreviewers: [hacker]\nmax_review_rounds: 9\n");

        Assert.False(result.IsValid);
        Assert.Contains("$.reviewers[0]", result.Error, StringComparison.Ordinal);
        Assert.Contains("; ", result.Error, StringComparison.Ordinal);
        Assert.Contains("$.max_review_rounds", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/reviewers/0", "$.reviewers[0]")]
    [InlineData("", "$")]
    [InlineData("/models/coder", "$.models.coder")]
    [InlineData("/a~1b/c~0d", "$.a/b.c~d")]
    public void Json_pointers_become_readable_paths(string pointer, string expected) =>
        Assert.Equal(expected, RepoConfigValidator.JsonPath(pointer));

    [Fact]
    public void The_public_schema_file_is_the_embedded_one()
    {
        var root = FindRepositoryRoot();
        var onDisk = File.ReadAllText(Path.Combine(root, "schemas", "codenetix.v1.json"));
        using var stream = typeof(RepoConfigValidator).Assembly.GetManifestResourceStream("CodeNetix.Pipeline.codenetix.v1.json")!;
        using var reader = new StreamReader(stream);

        Assert.Equal(onDisk, reader.ReadToEnd());
        Assert.Contains("\"additionalProperties\": false", onDisk, StringComparison.Ordinal);
        Assert.Contains("https://json-schema.org/draft/2020-12/schema", onDisk, StringComparison.Ordinal);
    }

    // --- Cálculo de la configuración efectiva ---------------------------------------------

    private static InstanceLimits Instance(int rounds = 2, int attempts = 3, decimal? budget = null) =>
        new() { MaxReviewRounds = rounds, MaxCodingAttempts = attempts, BudgetUsd = budget };

    private static RepoConfigFile Repo(string yaml)
    {
        var result = RepoConfigValidator.Validate(yaml);
        Assert.True(result.IsValid, result.Error);
        return result.Config!;
    }

    [Fact]
    public void Without_file_the_defaults_apply()
    {
        var config = EffectiveConfigCalculator.Calculate(Instance(), null);

        Assert.Equal(ConfigSource.Defaults, config.Source);
        Assert.Equal([ReviewerKind.Security, ReviewerKind.Quality, ReviewerKind.Tests], config.Reviewers);
        Assert.Equal(["**/Migrations/**", "**/*.csproj", ".github/**"], config.ProtectedPaths);
        Assert.Equal(2, config.MaxReviewRounds);
        Assert.Equal(3, config.MaxCodingAttempts);
        Assert.Null(config.BudgetUsd);
        Assert.Null(config.Models);
    }

    [Fact]
    public void Valid_file_restricts_reviewers_and_rounds()
    {
        var config = EffectiveConfigCalculator.Calculate(Instance(), Repo("version: 1\nreviewers: [security]\nmax_review_rounds: 1\n"));

        Assert.Equal(ConfigSource.File, config.Source);
        Assert.Equal([ReviewerKind.Security], config.Reviewers);
        Assert.Equal(1, config.MaxReviewRounds);
        Assert.Equal(3, config.MaxCodingAttempts);
        Assert.Equal(EffectiveConfig.DefaultProtectedPaths, config.ProtectedPaths);
    }

    [Theory]
    [InlineData(2, 5, 2)] // ampliar no se puede
    [InlineData(2, 1, 1)]
    [InlineData(4, 3, 3)]
    [InlineData(1, 5, 1)]
    public void Limits_are_the_minimum_of_instance_and_repository(int instance, int repo, int expected)
    {
        var config = EffectiveConfigCalculator.Calculate(
            Instance(rounds: instance, attempts: instance),
            Repo($"version: 1\nmax_review_rounds: {repo}\nmax_coding_attempts: {repo}\n"));

        Assert.Equal(expected, config.MaxReviewRounds);
        Assert.Equal(expected, config.MaxCodingAttempts);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "3", "3")] // una instancia sin límite admite que el repositorio se limite
    [InlineData("5", "10", "5")]
    [InlineData("5", "2.5", "2.5")]
    [InlineData("5", null, "5")]
    public void Budget_is_the_minimum_and_null_means_unlimited(string? instance, string? repo, string? expected)
    {
        var file = Repo(repo is null ? "version: 1\n" : $"version: 1\nbudget_usd: {repo}\n");
        var config = EffectiveConfigCalculator.Calculate(
            Instance(budget: instance is null ? null : decimal.Parse(instance, System.Globalization.CultureInfo.InvariantCulture)),
            file);

        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), config.BudgetUsd);
    }

    [Fact]
    public void Repository_paths_are_added_and_defaults_are_never_removed()
    {
        var config = EffectiveConfigCalculator.Calculate(
            Instance(),
            Repo("version: 1\nprotected_paths: [\"src/Auth/**\", \"**/*.csproj\"]\n"));

        Assert.Equal(["**/Migrations/**", "**/*.csproj", ".github/**", "src/Auth/**"], config.ProtectedPaths);
    }

    [Fact]
    public void An_empty_protected_paths_list_keeps_the_defaults()
    {
        var config = EffectiveConfigCalculator.Calculate(Instance(), Repo("version: 1\nprotected_paths: []\n"));

        Assert.Equal(EffectiveConfig.DefaultProtectedPaths, config.ProtectedPaths);
    }

    [Fact]
    public void Models_come_from_the_repository_or_are_null()
    {
        var withModels = EffectiveConfigCalculator.Calculate(Instance(), Repo("version: 1\nmodels:\n  coder: c1\n"));
        var emptyModels = EffectiveConfigCalculator.Calculate(Instance(), Repo("version: 1\nmodels: {}\n"));

        Assert.Equal(new AgentModels(null, "c1", null), withModels.Models);
        Assert.Null(emptyModels.Models);
    }

    [Fact]
    public void Effective_config_round_trips_through_its_json_snapshot()
    {
        var config = EffectiveConfigCalculator.Calculate(
            Instance(budget: 4m),
            Repo("version: 1\nreviewers: [tests, quality]\nprotected_paths: [a/**]\nmodels:\n  analyst: a\n"));

        var json = EffectiveConfigSerializer.Serialize(config);
        var back = EffectiveConfigSerializer.Deserialize(json);

        Assert.Contains("\"reviewers\":[\"tests\",\"quality\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"file\"", json, StringComparison.Ordinal);
        Assert.Equal(config.ProtectedPaths, back.ProtectedPaths);
        Assert.Equal(config.Reviewers, back.Reviewers);
        Assert.Equal(config with { ProtectedPaths = back.ProtectedPaths, Reviewers = back.Reviewers }, back);
    }

    // --- Límites de la instancia ----------------------------------------------------------

    [Theory]
    [InlineData(0, 3, "1", InstanceOptionsValidator.InvalidMaxReviewRounds)]
    [InlineData(2, 0, "1", InstanceOptionsValidator.InvalidMaxCodingAttempts)]
    [InlineData(2, 3, "0", InstanceOptionsValidator.InvalidBudgetUsd)]
    public void Invalid_instance_limits_put_the_instance_in_Diagnostic(int rounds, int attempts, string budget, string reason)
    {
        var options = new InstanceOptions
        {
            GitHub = { Token = "ghp_x" },
            Providers = { ["Anthropic"] = new ProviderInstanceOptions { ApiKey = "k" } },
            Limits = { MaxReviewRounds = rounds, MaxCodingAttempts = attempts, BudgetUsd = decimal.Parse(budget, System.Globalization.CultureInfo.InvariantCulture) },
        };

        Assert.Equal([reason], InstanceOptionsValidator.Validate(options));
    }

    [Fact]
    public void Default_instance_limits_are_valid()
    {
        var options = new InstanceOptions
        {
            GitHub = { Token = "ghp_x" },
            Providers = { ["Anthropic"] = new ProviderInstanceOptions { ApiKey = "k" } },
        };

        Assert.Empty(InstanceOptionsValidator.Validate(options));
        Assert.Equal(2, options.Limits.MaxReviewRounds);
        Assert.Equal(3, options.Limits.MaxCodingAttempts);
        Assert.Null(options.Limits.BudgetUsd);
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeNetix.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No se encuentra CodeNetix.slnx.");
    }
}
