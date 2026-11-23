using CodeNetix.Agents;
using CodeNetix.GitHub;
using CodeNetix.Host.Secrets;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Configuration;

/// <summary>
/// Claves desde el Panel (historia 8.1), solo en <c>serve</c> (<c>action</c> sigue usando únicamente los <i>secrets</i>
/// del workflow):
/// <list type="bullet">
/// <item>almacén cifrado, validador y servicio;</item>
/// <item>las claves guardadas rellenan los huecos de <see cref="InstanceOptions"/> y <see cref="GitHubOptions"/> (el
/// entorno y el YAML tienen prioridad);</item>
/// <item><c>IOptions&lt;T&gt;</c> de esas opciones y de <see cref="AgentsOptions"/> leen el valor vigente del monitor, para
/// aplicar una clave nueva sin reiniciar;</item>
/// <item>el redactor conoce las claves guardadas desde el arranque.</item>
/// </list>
/// </summary>
internal static class PanelSecretsComposition
{
    public static void AddPanelSecrets(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        services.AddSingleton<PanelSecretStore>();
        services.AddSingleton<SecretValidator>();
        services.AddSingleton<PanelSecretsService>();

        services.AddOptions<InstanceOptions>()
            .PostConfigure<PanelSecretStore, IConfiguration>((instance, store, configuration) =>
                PanelSecretCatalog.Apply(instance, store.Current, configuration));
        services.AddOptions<GitHubOptions>()
            .PostConfigure<PanelSecretStore, IConfiguration>((gitHub, store, configuration) =>
            {
                if (store.Current.Get(PanelSecretId.GitHubToken) is { } token
                    && !PanelSecretCatalog.IsSetByConfiguration(configuration, PanelSecretId.GitHubToken))
                {
                    gitHub.Token = token;
                }
            });

        // Registros cerrados: ganan al IOptions<> genérico de Microsoft.Extensions.Options.
        services.AddSingleton<IOptions<InstanceOptions>>(sp => new LiveOptions<InstanceOptions>(sp.GetRequiredService<IOptionsMonitor<InstanceOptions>>()));
        services.AddSingleton<IOptions<GitHubOptions>>(sp => new LiveOptions<GitHubOptions>(sp.GetRequiredService<IOptionsMonitor<GitHubOptions>>()));
        services.AddSingleton<IOptions<AgentsOptions>>(sp => new LiveOptions<AgentsOptions>(sp.GetRequiredService<IOptionsMonitor<AgentsOptions>>()));

        // Sustituye al de la composición común (el último registro gana): mismo redactor, más las claves guardadas.
        services.AddSingleton(sp =>
        {
            var redactor = new SecretRedactor(sp.GetRequiredService<IConfiguration>());
            redactor.AddSecrets(sp.GetRequiredService<PanelSecretStore>().Current.Values);
            return redactor;
        });
    }
}
