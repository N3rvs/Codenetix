namespace CodeNetix.Host.Configuration;

/// <summary>
/// Configuración de instancia: sección <c>CODENETIX</c>, que se rellena con las variables
/// <c>CODENETIX__*</c> y con el archivo opcional <c>/config/codenetix.instance.yml</c>.
/// </summary>
internal sealed class InstanceOptions
{
    public const string SectionName = "CODENETIX";

    public GitHubInstanceOptions GitHub { get; set; } = new();

    /// <summary>Acceso a la instancia (<c>CODENETIX__Host__*</c>, AD-17).</summary>
    public HostInstanceOptions Host { get; set; } = new();

    /// <summary>Proveedores por nombre (<c>CODENETIX__Providers__&lt;Nombre&gt;__ApiKey</c>).</summary>
    public Dictionary<string, ProviderInstanceOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Límites de la instancia (<c>CODENETIX__Limits__*</c>, FR-3). Pipeline enlaza la misma sección en su
    /// <c>InstanceLimits</c>; aquí solo se validan. <c>codenetix.yml</c> solo puede restringirlos.
    /// </summary>
    public LimitsInstanceOptions Limits { get; set; } = new();

    /// <summary>Agentes (<c>CODENETIX__Agents__*</c>): Proveedor y modelo por rol y reintentos de esquema.</summary>
    public AgentsInstanceOptions Agents { get; set; } = new();

    /// <summary>
    /// Sandbox (<c>CODENETIX__Sandbox__*</c>): imagen, endpoint de Docker y límites del contenedor. El módulo Sandbox
    /// enlaza la misma sección en sus <see cref="CodeNetix.Sandbox.SandboxOptions"/>; aquí solo se validan.
    /// </summary>
    public CodeNetix.Sandbox.SandboxOptions Sandbox { get; set; } = new();

    /// <summary>
    /// Tabla de precios por modelo (<c>CODENETIX__Pricing__&lt;Modelo&gt;__InputPerMillionUsd</c> /
    /// <c>OutputPerMillionUsd</c>, AD-15). Añade o sobreescribe las tarifas del catálogo por defecto.
    /// </summary>
    public Dictionary<string, ModelPricingInstanceOptions> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sondeo de issues etiquetadas (<c>CODENETIX__Polling__*</c>, FR-6). Solo lo usa <c>serve</c>.</summary>
    public PollingInstanceOptions Polling { get; set; } = new();

    /// <summary>Webhook de Sentry y correspondencia proyecto → repositorio (<c>CODENETIX__Sentry__*</c>, FR-7, FR-8). Solo <c>serve</c>.</summary>
    public SentryInstanceOptions Sentry { get; set; } = new();
}

internal sealed class SentryInstanceOptions
{
    public const string SectionName = InstanceOptions.SectionName + ":Sentry";

    public const string ProjectsKey = SectionName + ":Projects";

    /// <summary>
    /// <c>CODENETIX__Sentry__WebhookSecret</c>: Client Secret de la integración interna de Sentry. Sin valor,
    /// <c>:5001/webhooks/sentry</c> está desactivado (404). Con valor, al menos 16 caracteres. Nunca se registra.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// <c>CODENETIX__Sentry__Projects__&lt;slug&gt;=owner/repo</c>: Repositorio objetivo de cada proyecto de Sentry (AD-10: la
    /// única fuente de la correspondencia). Lo lee Host en crudo (claves sin distinguir mayúsculas).
    /// </summary>
    public Dictionary<string, string?> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>CODENETIX__Sentry__AuthToken</c> (historia 7.4): token de la API de Sentry (<c>event:read</c>, <c>project:read</c>).
    /// Sin valor, el contexto de Sentry para el Analista y <c>codenetix fix &lt;url-de-sentry&gt;</c> están desactivados.
    /// Nunca se registra.
    /// </summary>
    public string? AuthToken { get; set; }

    /// <summary><c>CODENETIX__Sentry__Organization</c>: slug de la organización de Sentry (obligatorio con <see cref="AuthToken"/>).</summary>
    public string? Organization { get; set; }

    /// <summary><c>CODENETIX__Sentry__BaseUrl</c>: por defecto <c>https://sentry.io</c> (Sentry autoalojado, OQ-5).</summary>
    public string? BaseUrl { get; set; }

    public string? EffectiveWebhookSecret => string.IsNullOrWhiteSpace(WebhookSecret) ? null : WebhookSecret.Trim();

    public string? EffectiveAuthToken => string.IsNullOrWhiteSpace(AuthToken) ? null : AuthToken.Trim();

    public string? EffectiveOrganization => string.IsNullOrWhiteSpace(Organization) ? null : Organization.Trim().ToLowerInvariant();

    public string EffectiveBaseUrl => string.IsNullOrWhiteSpace(BaseUrl)
        ? CodeNetix.Ingestion.Features.ReadSentryContext.SentryApiOptions.DefaultBaseUrl
        : BaseUrl.Trim();

    /// <summary>Pares (slug, valor) en crudo de la sección <c>Projects</c>.</summary>
    public static Dictionary<string, string?> ReadProjects(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in section.GetChildren())
        {
            result[child.Key] = child.Value;
        }

        return result;
    }
}

internal sealed class PollingInstanceOptions
{
    public const string SectionName = InstanceOptions.SectionName + ":Polling";

    public const string RepositoriesKey = SectionName + ":Repositories";

    public const int DefaultIntervalSeconds = 300;

    public const int MinIntervalSeconds = 60;

    public const int MaxIntervalSeconds = 86_400;

    public const int MaxRepositories = 100;

    public const int MaxLabelLength = 50;

    /// <summary>
    /// <c>CODENETIX__Polling__Repositories</c>: <c>owner/repo</c> separados por comas o <c>;</c>, o como
    /// <c>__0</c>, <c>__1</c>… (lo lee Host en crudo, no el binder). Vacío (por defecto) = sondeo desactivado.
    /// </summary>
    public List<string> Repositories { get; set; } = [];

    /// <summary>
    /// <c>CODENETIX__Polling__Label</c> (por defecto <c>codenetix</c>): la etiqueta de disparo de la instancia, compartida por
    /// el sondeo y el webhook de GitHub.
    /// </summary>
    public string? Label { get; set; }

    /// <summary><c>CODENETIX__Polling__IntervalSeconds</c> (por defecto 300, entre 60 y 86400).</summary>
    public int IntervalSeconds { get; set; } = DefaultIntervalSeconds;

    public string EffectiveLabel => string.IsNullOrWhiteSpace(Label) ? CodeNetix.Ingestion.Features.PollGitHub.GitHubPollingOptions.DefaultLabel : Label.Trim();

    /// <summary>Lista en crudo: cadena con separadores o hijos indexados; sin vacíos.</summary>
    public static List<string> ReadRepositories(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            return [.. section.Value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        return [.. section.GetChildren()
            .OrderBy(c => int.TryParse(c.Key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : int.MaxValue)
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)];
    }

    /// <summary><c>owner/repo</c> válido (letras, dígitos, <c>-</c>, <c>_</c>, <c>.</c>) → en minúsculas; si no, <c>false</c>.</summary>
    public static bool TryParseRepository(string? value, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        var parts = value?.Trim().Split('/');
        if (parts is not { Length: 2 }
            || !CodeNetix.Contracts.GitHubObjectKey.IsName(parts[0])
            || !CodeNetix.Contracts.GitHubObjectKey.IsName(parts[1])
            || parts[0].Length > 39
            || parts[1].Length > 100)
        {
            return false;
        }

        owner = parts[0].ToLowerInvariant();
        repo = parts[1].ToLowerInvariant();
        return true;
    }
}

internal sealed class ModelPricingInstanceOptions
{
    public decimal? InputPerMillionUsd { get; set; }

    public decimal? OutputPerMillionUsd { get; set; }
}

internal sealed class AgentsInstanceOptions
{
    /// <summary><c>CODENETIX__Agents__SchemaRetries</c>: reintentos de esquema tras el primer intento (0–5, por defecto 2).</summary>
    public int SchemaRetries { get; set; } = CodeNetix.Agents.AgentsOptions.DefaultSchemaRetries;

    /// <summary><c>CODENETIX__Agents__Analyst__*</c>.</summary>
    public AgentRoleInstanceOptions Analyst { get; set; } = new();

    /// <summary><c>CODENETIX__Agents__Coder__*</c>.</summary>
    public AgentRoleInstanceOptions Coder { get; set; } = new();

    /// <summary><c>CODENETIX__Agents__Reviewers__*</c> (general y overrides por especialidad).</summary>
    public ReviewersInstanceOptions Reviewers { get; set; } = new();
}

internal sealed class ReviewersInstanceOptions : AgentRoleInstanceOptions
{
    /// <summary><c>CODENETIX__Agents__Reviewers__Security__*</c>.</summary>
    public AgentRoleInstanceOptions Security { get; set; } = new();

    /// <summary><c>CODENETIX__Agents__Reviewers__Quality__*</c>.</summary>
    public AgentRoleInstanceOptions Quality { get; set; } = new();

    /// <summary><c>CODENETIX__Agents__Reviewers__Tests__*</c>.</summary>
    public AgentRoleInstanceOptions Tests { get; set; } = new();
}

internal class AgentRoleInstanceOptions
{
    /// <summary>
    /// <c>CODENETIX__Agents__&lt;Rol&gt;__Provider</c>: nombre de un Proveedor con key. Por defecto, el único con key,
    /// o <c>Anthropic</c> si hay varios.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// <c>CODENETIX__Agents__&lt;Rol&gt;__Model</c>: por defecto <c>claude-sonnet-5-5</c> (Anthropic) o <c>gpt-5</c>
    /// (OpenAI); obligatorio con cualquier otro Proveedor. <c>models.&lt;rol&gt;</c> de <c>codenetix.yml</c> lo sustituye.
    /// </summary>
    public string? Model { get; set; }
}

internal sealed class LimitsInstanceOptions
{
    public const string SectionName = InstanceOptions.SectionName + ":Limits";

    /// <summary><c>CODENETIX__Limits__MaxReviewRounds</c> (por defecto 2).</summary>
    public int MaxReviewRounds { get; set; } = CodeNetix.Pipeline.InstanceLimits.DefaultMaxReviewRounds;

    /// <summary><c>CODENETIX__Limits__MaxCodingAttempts</c> (por defecto 3).</summary>
    public int MaxCodingAttempts { get; set; } = CodeNetix.Pipeline.InstanceLimits.DefaultMaxCodingAttempts;

    /// <summary><c>CODENETIX__Limits__BudgetUsd</c>: sin valor, sin límite.</summary>
    public decimal? BudgetUsd { get; set; }
}

internal sealed class GitHubInstanceOptions
{
    public const string SectionName = InstanceOptions.SectionName + ":GitHub";

    public const string WebhookRepositoriesKey = SectionName + ":WebhookRepositories";

    public const int MinWebhookSecretLength = 16;

    /// <summary><c>CODENETIX__GitHub__Token</c>.</summary>
    public string? Token { get; set; }

    /// <summary>
    /// <c>CODENETIX__GitHub__WebhookSecret</c>: secreto del webhook de GitHub (FR-7). Sin valor, <c>:5001/webhooks/github</c>
    /// está desactivado (404). Con valor, al menos 16 caracteres. Nunca se registra.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// <c>CODENETIX__GitHub__WebhookRepositories</c>: <c>owner/repo</c> aceptados por el webhook, con el formato de
    /// <c>CODENETIX__Polling__Repositories</c> (lo lee Host en crudo). Vacío (por defecto) = cualquier repositorio.
    /// </summary>
    public List<string> WebhookRepositories { get; set; } = [];

    /// <summary>Secreto recortado; <c>null</c> si no hay.</summary>
    public string? EffectiveWebhookSecret => string.IsNullOrWhiteSpace(WebhookSecret) ? null : WebhookSecret.Trim();
}

internal sealed class HostInstanceOptions
{
    public const string SectionName = InstanceOptions.SectionName + ":Host";

    /// <summary>
    /// <c>CODENETIX__Host__Exposed</c>: la instancia se publica fuera de localhost. Exige un
    /// <see cref="AccessToken"/> explícito.
    /// </summary>
    public bool Exposed { get; set; }

    /// <summary>
    /// <c>CODENETIX__Host__AccessToken</c>: si tiene valor, es el <c>admin-token</c> de la instancia
    /// (en lugar del de <c>/data/admin.token</c>).
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// <c>CODENETIX__Host__Url</c>: URL de la API que usa el cliente <c>codenetix fix</c>
    /// (por defecto <c>http://127.0.0.1:5000</c>). <c>serve</c> no la usa.
    /// </summary>
    public string? Url { get; set; }
}

internal sealed class ProviderInstanceOptions
{
    /// <summary><c>CODENETIX__Providers__&lt;Nombre&gt;__ApiKey</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// <c>CODENETIX__Providers__&lt;Nombre&gt;__BaseUrl</c>: API compatible con OpenAI. Por defecto
    /// <c>https://api.anthropic.com/v1/</c> (Anthropic) y <c>https://api.openai.com/v1/</c> (OpenAI); obligatoria
    /// con cualquier otro nombre.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// <c>CODENETIX__Providers__&lt;Nombre&gt;__StructuredOutput</c>: el Proveedor admite salida estructurada
    /// (<c>json_schema</c>). Por defecto <c>true</c> en Anthropic y OpenAI y <c>false</c> en el resto (el esquema va
    /// entonces en el prompt).
    /// </summary>
    public bool? StructuredOutput { get; set; }
}
