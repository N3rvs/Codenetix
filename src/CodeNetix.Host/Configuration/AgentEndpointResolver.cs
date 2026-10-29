using CodeNetix.Agents;

namespace CodeNetix.Host.Configuration;

/// <summary>
/// Resuelve el Proveedor de cada rol a partir de la Configuración de instancia. Todos los Proveedores se tratan
/// como compatibles con OpenAI:
/// <list type="bullet">
/// <item>Proveedor: <c>CODENETIX__Agents__&lt;Rol&gt;__Provider</c>; si no, el único con key, o <c>Anthropic</c> si hay varios.</item>
/// <item>URL: <c>CODENETIX__Providers__&lt;Nombre&gt;__BaseUrl</c>; por defecto la de Anthropic u OpenAI, obligatoria en el resto.</item>
/// <item>Salida estructurada: <c>__StructuredOutput</c>; por defecto <c>true</c> en Anthropic y OpenAI, <c>false</c> en el resto.</item>
/// <item>Modelo: <c>CODENETIX__Agents__&lt;Rol&gt;__Model</c>; por defecto <c>claude-sonnet-5-5</c> o <c>gpt-5</c>, obligatorio en el resto.</item>
/// </list>
/// Una configuración incoherente devuelve los motivos (modo <c>Diagnostic</c>), que nombran variables y nunca valores secretos.
/// </summary>
internal static class AgentEndpointResolver
{
    public const string Anthropic = "Anthropic";
    public const string OpenAI = "OpenAI";

    public const string AnthropicBaseUrl = "https://api.anthropic.com/v1/";
    public const string OpenAIBaseUrl = "https://api.openai.com/v1/";

    public const string AnthropicDefaultModel = "claude-sonnet-5-5";
    public const string OpenAIDefaultModel = "gpt-5";

    public const string AnalystRole = "Analyst";
    public const string CoderRole = "Coder";

    public static (AgentEndpoint? Endpoint, IReadOnlyList<string> Reasons) ResolveAnalyst(InstanceOptions options) =>
        ResolveRole(
            options,
            AnalystRole,
            "el Analista",
            options?.Agents?.Analyst,
            fallbackModel: null);

    public static (AgentEndpoint? Endpoint, IReadOnlyList<string> Reasons) ResolveCoder(InstanceOptions options)
    {
        var coder = options?.Agents?.Coder;
        if (!string.IsNullOrWhiteSpace(coder?.Provider))
        {
            return ResolveRole(
                options,
                CoderRole,
                "el Programador",
                coder,
                fallbackModel: null);
        }

        var analyst = options?.Agents?.Analyst;
        var effectiveCoder = new AgentRoleInstanceOptions
        {
            Provider = analyst?.Provider,
            Model = coder?.Model,
        };
        var fallbackModel = analyst?.Model?.Trim() is { Length: > 0 } analystModel ? analystModel : null;
        return ResolveRole(
            options,
            CoderRole,
            "el Programador",
            effectiveCoder,
            fallbackModel);
    }

    private static (AgentEndpoint? Endpoint, IReadOnlyList<string> Reasons) ResolveRole(
        InstanceOptions? options,
        string roleName,
        string roleLabel,
        AgentRoleInstanceOptions? roleOptions,
        string? fallbackModel)
    {
        ArgumentNullException.ThrowIfNull(options);

        var providers = (options.Providers ?? []).Where(p => p.Value is not null).ToList();
        var withKey = providers.Where(p => !string.IsNullOrWhiteSpace(p.Value.ApiKey)).ToList();
        if (withKey.Count == 0)
        {
            // Ya lo informa InstanceOptionsValidator.MissingProviderApiKey.
            return (null, []);
        }

        var role = roleOptions ?? new AgentRoleInstanceOptions();
        var configured = role.Provider?.Trim();
        string name;
        if (!string.IsNullOrEmpty(configured))
        {
            name = configured;
        }
        else if (withKey.Count == 1)
        {
            name = withKey[0].Key;
        }
        else
        {
            name = Anthropic;
        }

        var entry = withKey.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
        if (entry.Value is null)
        {
            var reason = string.IsNullOrEmpty(configured)
                ? $"Hay varios Proveedores con key y ninguno es {Anthropic}: indica cuál usa {roleLabel} con CODENETIX__Agents__{roleName}__Provider."
                : $"CODENETIX__Agents__{roleName}__Provider nombra el Proveedor '{name}', que no tiene key: define CODENETIX__Providers__{name}__ApiKey.";
            return (null, [reason]);
        }

        name = entry.Key;
        var provider = entry.Value;
        var reasons = new List<string>();

        var rawBaseUrl = string.IsNullOrWhiteSpace(provider.BaseUrl) ? DefaultBaseUrl(name) : provider.BaseUrl.Trim();
        Uri? baseUrl = null;
        if (rawBaseUrl is null)
        {
            reasons.Add($"Falta la URL del Proveedor '{name}': define CODENETIX__Providers__{name}__BaseUrl (una API compatible con OpenAI, p. ej. http://localhost:11434/v1/).");
        }
        else if (!Uri.TryCreate(rawBaseUrl.EndsWith('/') ? rawBaseUrl : rawBaseUrl + "/", UriKind.Absolute, out baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(baseUrl.UserInfo)
            || !string.IsNullOrEmpty(baseUrl.Query))
        {
            baseUrl = null;
            reasons.Add($"CODENETIX__Providers__{name}__BaseUrl no es una URL http(s) válida (sin credenciales ni parámetros).");
        }

        var model = string.IsNullOrWhiteSpace(role.Model) ? DefaultModel(name) ?? fallbackModel : role.Model.Trim();
        if (model is null)
        {
            reasons.Add($"Falta el modelo de {roleLabel} para el Proveedor '{name}': define CODENETIX__Agents__{roleName}__Model.");
        }

        if (reasons.Count > 0 || baseUrl is null || model is null)
        {
            return (null, reasons);
        }

        var structuredOutput = provider.StructuredOutput ?? IsKnown(name);
        return (new AgentEndpoint(name, baseUrl, provider.ApiKey!.Trim(), model, structuredOutput), []);
    }

    private static bool IsKnown(string name) =>
        string.Equals(name, Anthropic, StringComparison.OrdinalIgnoreCase) || string.Equals(name, OpenAI, StringComparison.OrdinalIgnoreCase);

    private static string? DefaultBaseUrl(string name) =>
        string.Equals(name, Anthropic, StringComparison.OrdinalIgnoreCase) ? AnthropicBaseUrl
        : string.Equals(name, OpenAI, StringComparison.OrdinalIgnoreCase) ? OpenAIBaseUrl
        : null;

    private static string? DefaultModel(string name) =>
        string.Equals(name, Anthropic, StringComparison.OrdinalIgnoreCase) ? AnthropicDefaultModel
        : string.Equals(name, OpenAI, StringComparison.OrdinalIgnoreCase) ? OpenAIDefaultModel
        : null;
}
