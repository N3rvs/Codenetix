namespace CodeNetix.Agents;

/// <summary>
/// Opciones del módulo Agents. Host las resuelve a partir de la configuración de instancia
/// (<c>CODENETIX__Providers__*</c> y <c>CODENETIX__Agents__*</c>); solo Host lee el entorno.
/// </summary>
public sealed class AgentsOptions
{
    public const int DefaultSchemaRetries = 2;
    public const int MaxSchemaRetries = 5;

    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(120);

    /// <summary><c>CODENETIX__Agents__SchemaRetries</c>: reintentos tras el primer intento (0–5, por defecto 2).</summary>
    public int SchemaRetries { get; set; } = DefaultSchemaRetries;

    /// <summary>Tiempo máximo de cada llamada al Proveedor (120 s; las pruebas pueden acortarlo).</summary>
    public TimeSpan CallTimeout { get; set; } = DefaultCallTimeout;

    /// <summary>Proveedor y modelo del Analista; <c>null</c> si la configuración no es coherente (modo diagnóstico).</summary>
    public AgentEndpoint? Analyst { get; set; }
}

/// <summary>Un Proveedor compatible con OpenAI ya resuelto para un rol.</summary>
/// <param name="Provider">Nombre del Proveedor (<c>Anthropic</c>, <c>OpenAI</c>, …), solo informativo.</param>
/// <param name="BaseUrl">Base de la API compatible con OpenAI (p. ej. <c>https://api.openai.com/v1/</c>).</param>
/// <param name="ApiKey">Nunca se registra ni aparece en errores, eventos ni BD.</param>
/// <param name="Model">Modelo por defecto del rol (la <c>EffectiveConfig</c> puede sustituirlo).</param>
/// <param name="StructuredOutput">El Proveedor admite <c>response_format: json_schema</c>.</param>
public sealed record AgentEndpoint(string Provider, Uri BaseUrl, string ApiKey, string Model, bool StructuredOutput)
{
    /// <summary>Sin la key: los records generan un <c>ToString</c> con todas las propiedades.</summary>
    public override string ToString() => $"{Provider} ({BaseUrl}, {Model})";
}
