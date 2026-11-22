namespace CodeNetix.Ingestion.Features.PollGitHub;

/// <summary>
/// Sondeo de issues etiquetadas (FR-6). Las rellena Host desde <c>CODENETIX__Polling__*</c> (ya validadas); sin
/// repositorios, el sondeo está desactivado y no llama a GitHub.
/// </summary>
public sealed class GitHubPollingOptions
{
    public const string DefaultLabel = "codenetix";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    /// <summary>Espera máxima tras un rate limit, aunque GitHub pida más.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    /// <summary>Repositorios a sondear (owner y repo en minúsculas, sin duplicados).</summary>
    public List<PolledRepository> Repositories { get; } = [];

    public string Label { get; set; } = DefaultLabel;

    public TimeSpan Interval { get; set; } = DefaultInterval;

    public bool Enabled => Repositories.Count > 0;
}

/// <summary>Repositorio objetivo del sondeo.</summary>
public sealed record PolledRepository(string Owner, string Repo)
{
    public string FullName => $"{Owner}/{Repo}";
}
