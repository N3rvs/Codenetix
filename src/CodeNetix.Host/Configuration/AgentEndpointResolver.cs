using CodeNetix.Agents;
using CodeNetix.Contracts;

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
    public const string ReviewersRole = "Reviewers";

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

    public static (AgentEndpoint? Endpoint, IReadOnlyList<string> Reasons) ResolveReviewers(InstanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var reviewers = options.Agents?.Reviewers;
        if (string.IsNullOrWhiteSpace(reviewers?.Provider) && string.IsNullOrWhiteSpace(reviewers?.Model))
        {
            return ResolveAnalyst(options);
        }

        if (!string.IsNullOrWhiteSpace(reviewers?.Provider))
        {
            return ResolveRole(
                options,
                ReviewersRole,
                "los Revisores",
                reviewers,
                fallbackModel: null);
        }

        var analyst = options?.Agents?.Analyst;
        var effective = new AgentRoleInstanceOptions
        {
            Provider = analyst?.Provider,
            Model = reviewers?.Model,
        };
        var fallbackModel = analyst?.Model?.Trim() is { Length: > 0 } analystModel ? analystModel : null;
        return ResolveRole(
            options,
            ReviewersRole,
            "los Revisores",
            effective,
            fallbackModel);
    }

    public static (AgentEndpoint? Endpoint, IReadOnlyList<string> Reasons) ResolveReviewer(InstanceOptions options, ReviewerKind kind)
    {
        var reviewers = options?.Agents?.Reviewers;
        var specific = kind switch
        {
            ReviewerKind.Security => reviewers?.Security,
            ReviewerKind.Quality => reviewers?.Quality,
            ReviewerKind.Tests => reviewers?.Tests,
            _ => null,
        };

        if (specific is null || (string.IsNullOrWhiteSpace(specific.Provider) && string.IsNullOrWhiteSpace(specific.Model)))
        {
            return (null, []);
        }

        var roleName = $"{ReviewersRole}__{kind}";
        var roleLabel = $"el Revisor {kind}";

        if (!string.IsNullOrWhiteSpace(specific.Provider))
        {
            return ResolveRole(options, roleName, roleLabel, specific, fallbackModel: null);
        }

        var inheritedProvider = !string.IsNullOrWhiteSpace(reviewers?.Provider)
            ? reviewers!.Provider
            : options?.Agents?.Analyst?.Provider;
        var effective = new AgentRoleInstanceOptions
        {
            Provider = inheritedProvider,
            Model = specific.Model,
        };
        return ResolveRole(options, roleName, roleLabel, effective, fallbackModel: null);
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

        var baseUrl = ResolveBaseUrl(name, provider, out var baseUrlReason);
        if (baseUrlReason is not null)
        {
            reasons.Add(baseUrlReason);
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

    /// <summary>
    /// URL base efectiva del Proveedor <paramref name="name"/> (<c>CODENETIX__Providers__&lt;Nombre&gt;__BaseUrl</c> o la de
    /// Anthropic/OpenAI), terminada en <c>/</c>; <c>null</c> con el motivo si falta o no es válida. La usa también la
    /// validación de keys del Panel (historia 8.1), que llama al mismo host que usarán los agentes.
    /// </summary>
    public static Uri? ResolveBaseUrl(string name, ProviderInstanceOptions? provider, out string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        reason = null;
        var rawBaseUrl = provider?.BaseUrl is { } configured && !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : DefaultBaseUrl(name);
        if (rawBaseUrl is null)
        {
            reason = $"Falta la URL del Proveedor '{name}': define CODENETIX__Providers__{name}__BaseUrl (una API compatible con OpenAI, p. ej. http://localhost:11434/v1/).";
            return null;
        }

        if (!Uri.TryCreate(rawBaseUrl.EndsWith('/') ? rawBaseUrl : rawBaseUrl + "/", UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(baseUrl.UserInfo)
            || !string.IsNullOrEmpty(baseUrl.Query))
        {
            reason = $"CODENETIX__Providers__{name}__BaseUrl no es una URL http(s) válida (sin credenciales ni parámetros).";
            return null;
        }

        return baseUrl;
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
