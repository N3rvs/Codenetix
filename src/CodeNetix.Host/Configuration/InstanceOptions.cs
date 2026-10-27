namespace CodeNetix.Host.Configuration;

/// <summary>
/// Configuración de instancia: sección <c>CODENETIX</c>, que se rellena con las variables
/// <c>CODENETIX__*</c> y con el archivo opcional <c>/config/codenetix.instance.yml</c>.
/// </summary>
internal sealed class InstanceOptions
{
    public const string SectionName = "CODENETIX";

    public GitHubInstanceOptions GitHub { get; set; } = new();

    /// <summary>Proveedores por nombre (<c>CODENETIX__Providers__&lt;Nombre&gt;__ApiKey</c>).</summary>
    public Dictionary<string, ProviderInstanceOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class GitHubInstanceOptions
{
    /// <summary><c>CODENETIX__GitHub__Token</c>.</summary>
    public string? Token { get; set; }
}

internal sealed class ProviderInstanceOptions
{
    /// <summary><c>CODENETIX__Providers__&lt;Nombre&gt;__ApiKey</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary><c>CODENETIX__Providers__&lt;Nombre&gt;__BaseUrl</c> (opcional).</summary>
    public string? BaseUrl { get; set; }
}
