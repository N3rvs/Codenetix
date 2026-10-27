namespace CodeNetix.Host.Configuration;

/// <summary>
/// Comprueba lo obligatorio de la Configuración de instancia. Cada motivo nombra la variable
/// que falta y nunca incluye un valor.
/// </summary>
internal static class InstanceOptionsValidator
{
    public const string MissingGitHubToken =
        "Falta el token de GitHub: define la variable CODENETIX__GitHub__Token.";

    public const string MissingProviderApiKey =
        "Falta la key de al menos un Proveedor: define la variable CODENETIX__Providers__<Nombre>__ApiKey (por ejemplo, CODENETIX__Providers__Anthropic__ApiKey).";

    public static IReadOnlyList<string> Validate(InstanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(options.GitHub?.Token))
        {
            reasons.Add(MissingGitHubToken);
        }

        if (options.Providers is null || !options.Providers.Values.Any(p => !string.IsNullOrWhiteSpace(p?.ApiKey)))
        {
            reasons.Add(MissingProviderApiKey);
        }

        return reasons;
    }
}
