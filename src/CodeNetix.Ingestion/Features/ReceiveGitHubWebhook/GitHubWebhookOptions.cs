namespace CodeNetix.Ingestion.Features.ReceiveGitHubWebhook;

/// <summary>
/// Webhook de GitHub (FR-7). Las rellena Host desde <c>CODENETIX__GitHub__WebhookSecret</c>,
/// <c>CODENETIX__GitHub__WebhookRepositories</c> y la etiqueta de disparo (<c>CODENETIX__Polling__Label</c>, compartida con el
/// sondeo). Sin secreto, el webhook está desactivado.
/// </summary>
public sealed class GitHubWebhookOptions
{
    /// <summary>Tamaño máximo del cuerpo (1 MiB): una issue cabe de sobra (el cuerpo de una issue tiene como mucho 65 536 caracteres).</summary>
    public const int DefaultMaxBodyBytes = 1024 * 1024;

    public const int DefaultQueueCapacity = 1000;

    public const int DefaultMaxConcurrency = 4;

    /// <summary>
    /// Margen con el que un evento <c>labeled</c> anterior al <c>updated_at</c> del payload se considera otro (antiguo). Las
    /// dos fechas son de GitHub (precisión de segundos), así que basta un margen pequeño; uno grande haría que un reetiquetado
    /// rápido se resolviera al etiquetado anterior, ya registrado, y se perdiera.
    /// </summary>
    public static readonly TimeSpan DefaultFreshnessTolerance = TimeSpan.FromSeconds(5);

    /// <summary>Esperas entre intentos de resolver el evento <c>labeled</c> (5 intentos, ~37 s).</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)];

    /// <summary>Secreto del webhook (recortado); <c>null</c> = desactivado. Nunca se registra.</summary>
    public string? Secret { get; set; }

    public string Label { get; set; } = PollGitHub.GitHubPollingOptions.DefaultLabel;

    /// <summary><c>owner/repo</c> en minúsculas aceptados; vacío = cualquiera.</summary>
    public HashSet<string> Repositories { get; } = new(StringComparer.Ordinal);

    public int MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;

    public int QueueCapacity { get; set; } = DefaultQueueCapacity;

    public int MaxConcurrency { get; set; } = DefaultMaxConcurrency;

    public TimeSpan FreshnessTolerance { get; set; } = DefaultFreshnessTolerance;

    public List<TimeSpan> RetryDelays { get; } = [.. DefaultRetryDelays];

    public bool Enabled => !string.IsNullOrEmpty(Secret);
}
