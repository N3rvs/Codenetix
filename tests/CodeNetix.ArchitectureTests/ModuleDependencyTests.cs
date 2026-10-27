namespace CodeNetix.ArchitectureTests;

public sealed class ModuleDependencyTests
{
    private static IReadOnlyList<ProjectInfo> LoadRealProjects() =>
        ModuleDependencyRule.LoadSourceProjects(ModuleDependencyRule.FindSolutionRoot(AppContext.BaseDirectory));

    [Fact]
    public void Src_contains_exactly_the_modules_of_AD1()
    {
        var names = LoadRealProjects().Select(p => p.Name).ToList();

        Assert.Equal(ModuleDependencyRule.ExpectedProjects, names);
    }

    [Fact]
    public void Business_modules_only_reference_Contracts()
    {
        var violations = ModuleDependencyRule.FindViolations(LoadRealProjects());

        Assert.True(violations.Count == 0, "Referencias prohibidas:\n" + string.Join("\n", violations));
    }

    public static TheoryData<string, ProjectInfo[], string[]> Cases => new()
    {
        {
            "módulo -> Contracts es válido",
            [new("CodeNetix.Pipeline", ["CodeNetix.Contracts"])],
            []
        },
        {
            "Host puede referenciar a todos",
            [new("CodeNetix.Host", ["CodeNetix.Contracts", "CodeNetix.Pipeline", "CodeNetix.Ingestion"])],
            []
        },
        {
            "Pipeline -> Ingestion es una violación",
            [new("CodeNetix.Pipeline", ["CodeNetix.Contracts", "CodeNetix.Ingestion"])],
            ["CodeNetix.Pipeline -> CodeNetix.Ingestion"]
        },
        {
            "módulo -> Host es una violación",
            [new("CodeNetix.GitHub", ["CodeNetix.Host"])],
            ["CodeNetix.GitHub -> CodeNetix.Host"]
        },
        {
            "Contracts sin referencias",
            [new("CodeNetix.Contracts", ["CodeNetix.Persistence"])],
            ["CodeNetix.Contracts -> CodeNetix.Persistence"]
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void FindViolations_detects_forbidden_references(string scenario, ProjectInfo[] projects, string[] expectedPrefixes)
    {
        var violations = ModuleDependencyRule.FindViolations(projects);

        Assert.True(expectedPrefixes.Length == violations.Count, $"{scenario}: {string.Join("; ", violations)}");
        foreach (var prefix in expectedPrefixes)
        {
            Assert.Contains(violations, v => v.StartsWith(prefix, StringComparison.Ordinal));
        }
    }
}
