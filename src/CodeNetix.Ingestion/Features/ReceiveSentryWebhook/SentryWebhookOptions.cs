using System.Text.RegularExpressions;

namespace CodeNetix.Ingestion.Features.ReceiveSentryWebhook;

/// <summary>Repositorio objetivo de un proyecto de Sentry (owner y repo en minúsculas).</summary>
public sealed record SentryTargetRepository(string Owner, string Repo)
{
    public string FullName => $"{Owner}/{Repo}";
}

/// <summary>
/// Webhook de Sentry (FR-7, FR-8). Las rellena Host desde <c>CODENETIX__Sentry__WebhookSecret</c> y
/// <c>CODENETIX__Sentry__Projects__&lt;slug&gt;=owner/repo</c> (la única fuente de la correspondencia, AD-10). Sin secreto,
/// el webhook está desactivado.
/// </summary>
public sealed partial class SentryWebhookOptions
{
    public const int DefaultMaxBodyBytes = 1024 * 1024;

    public const int DefaultQueueCapacity = 1000;

    public const int MaxProjects = 100;

    /// <summary>Esperas entre intentos de asegurar la issue de seguimiento (5 intentos, ~37 s).</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)];

    /// <summary>Client Secret de la integración de Sentry (recortado); <c>null</c> = desactivado. Nunca se registra.</summary>
    public string? Secret { get; set; }

    /// <summary>Slug de proyecto (en minúsculas) → Repositorio objetivo.</summary>
    public Dictionary<string, SentryTargetRepository> Projects { get; } = new(StringComparer.Ordinal);

    public int MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;

    public int QueueCapacity { get; set; } = DefaultQueueCapacity;

    public List<TimeSpan> RetryDelays { get; } = [.. DefaultRetryDelays];

    /// <summary>Espera máxima ante un rate limit de GitHub (se respeta su <c>Retry-After</c> hasta este tope).</summary>
    public static readonly TimeSpan DefaultMaxRetryAfter = TimeSpan.FromMinutes(5);

    public TimeSpan MaxRetryAfter { get; set; } = DefaultMaxRetryAfter;

    public bool Enabled => !string.IsNullOrEmpty(Secret);

    /// <summary>Slug de proyecto de Sentry: letras minúsculas, dígitos, <c>-</c> y <c>_</c>, hasta 100 caracteres.</summary>
    public static bool IsProjectSlug(string? value) => value is not null && SlugPattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
