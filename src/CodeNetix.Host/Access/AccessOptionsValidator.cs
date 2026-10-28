using CodeNetix.Host.Configuration;

namespace CodeNetix.Host.Access;

/// <summary>
/// Regla de exposición (AD-17): con <c>CODENETIX__Host__Exposed=true</c> el <c>AccessToken</c> es
/// obligatorio y de al menos <see cref="MinimumExposedTokenLength"/> caracteres. Se comprueba antes
/// de construir el host; el mensaje nombra ambas variables y nunca incluye un valor.
/// </summary>
internal static class AccessOptionsValidator
{
    public const int MinimumExposedTokenLength = 16;

    public const string ExposedWithoutAccessToken =
        "CODENETIX__Host__Exposed=true exige un token explícito: define CODENETIX__Host__AccessToken con al menos 16 caracteres.";

    /// <summary>Devuelve el motivo del rechazo, o <c>null</c> si la configuración de acceso es válida.</summary>
    public static string? Validate(HostInstanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Exposed)
        {
            return null;
        }

        var token = options.AccessToken;
        return string.IsNullOrWhiteSpace(token) || token.Trim().Length < MinimumExposedTokenLength
            ? ExposedWithoutAccessToken
            : null;
    }

    /// <summary>Lee la sección <c>CODENETIX:Host</c> y lanza <see cref="AccessConfigurationException"/> si no es válida.</summary>
    public static void EnsureValid(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        HostInstanceOptions options;
        try
        {
            options = configuration.GetSection(HostInstanceOptions.SectionName).Get<HostInstanceOptions>() ?? new();
        }
        catch (InvalidOperationException)
        {
            // Valor no booleano en Exposed: el mensaje del binder citaría el valor.
            throw new AccessConfigurationException("CODENETIX__Host__Exposed debe ser 'true' o 'false'.");
        }

        var reason = Validate(options);
        if (reason is not null)
        {
            throw new AccessConfigurationException(reason);
        }
    }
}

/// <summary>Configuración de acceso inválida: el arranque se aborta antes de construir el host.</summary>
internal sealed class AccessConfigurationException(string message) : Exception(message);
