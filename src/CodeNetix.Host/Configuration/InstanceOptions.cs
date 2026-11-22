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
    /// <summary><c>CODENETIX__GitHub__Token</c>.</summary>
    public string? Token { get; set; }
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
