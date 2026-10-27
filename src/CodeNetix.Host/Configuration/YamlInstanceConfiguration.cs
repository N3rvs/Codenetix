using Microsoft.Extensions.Configuration.EnvironmentVariables;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CodeNetix.Host.Configuration;

internal static class YamlInstanceConfigurationExtensions
{
    public const string DefaultPath = "/config/codenetix.instance.yml";

    /// <summary>Clave que sobrescribe la ruta del archivo (<c>CODENETIX__InstanceConfigPath</c>).</summary>
    public const string PathKey = InstanceOptions.SectionName + ":InstanceConfigPath";

    /// <summary>
    /// Añade el archivo de instancia bajo la sección <c>CODENETIX</c>, justo antes de la fuente de
    /// variables de entorno sin prefijo: el entorno (y la línea de comandos) tienen prioridad.
    /// Lanza <see cref="InstanceConfigurationException"/> si el archivo existe y no es válido, o si
    /// no es <paramref name="optional"/> y no existe.
    /// </summary>
    public static IConfigurationBuilder AddInstanceConfigFile(this IConfigurationBuilder configuration, string path, bool optional)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var source = new YamlInstanceConfigurationSource(path, optional);
        var sources = configuration.Sources;

        // La fuente sin prefijo es la que lee CODENETIX__*; la de DOTNET_ (prefijada) va antes de appsettings.
        var index = IndexOfEnvironmentSource(sources, unprefixedOnly: true);
        if (index < 0)
        {
            index = IndexOfEnvironmentSource(sources, unprefixedOnly: false);
        }

        if (index < 0)
        {
            sources.Add(source);
        }
        else
        {
            sources.Insert(index, source);
        }

        return configuration;
    }

    /// <summary>
    /// Añade el archivo de instancia según la configuración ya cargada: la ruta por defecto es
    /// opcional; una ruta indicada en <c>CODENETIX__InstanceConfigPath</c> es obligatoria.
    /// </summary>
    public static IConfigurationBuilder AddInstanceConfigFile(this IConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var isExplicit = !string.IsNullOrWhiteSpace(configuration[PathKey]);
        return configuration.AddInstanceConfigFile(configuration.ResolveInstanceConfigPath(), optional: !isExplicit);
    }

    private static int IndexOfEnvironmentSource(IList<IConfigurationSource> sources, bool unprefixedOnly)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource env
                && (!unprefixedOnly || string.IsNullOrEmpty(env.Prefix)))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Ruta efectiva del archivo según la configuración ya cargada.</summary>
    public static string ResolveInstanceConfigPath(this IConfiguration configuration)
    {
        var configured = configuration[PathKey];
        return string.IsNullOrWhiteSpace(configured) ? DefaultPath : configured;
    }
}

internal sealed class YamlInstanceConfigurationSource(string path, bool optional) : IConfigurationSource
{
    public string Path { get; } = path;

    public bool Optional { get; } = optional;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new YamlInstanceConfigurationProvider(Path, Optional);
}

/// <summary>
/// Proveedor mínimo: aplana el YAML (mapas y listas) a claves de configuración con el prefijo
/// <c>CODENETIX</c>. Si el archivo es opcional y no existe, no aporta nada. Los errores nombran
/// la ruta y la posición, nunca el contenido.
/// </summary>
internal sealed class YamlInstanceConfigurationProvider(string path, bool optional) : ConfigurationProvider
{
    private static readonly HashSet<string> PlainNulls = new(StringComparer.Ordinal) { "", "~", "null", "Null", "NULL" };

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            if (!optional)
            {
                throw new InstanceConfigurationException(
                    path,
                    Directory.Exists(path) ? "la ruta es un directorio, no un archivo" : "el archivo no existe");
            }

            Data = data;
            return;
        }

        var stream = new YamlStream();
        try
        {
            using var reader = new StreamReader(path);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new InstanceConfigurationException(path, $"YAML mal formado (línea {ex.Start.Line}, columna {ex.Start.Column})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstanceConfigurationException(path, "no se puede leer el archivo");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Claves duplicadas u otros errores del modelo: el mensaje original podría citar contenido.
            throw new InstanceConfigurationException(path, "estructura YAML no válida");
        }

        if (stream.Documents.Count > 1)
        {
            throw new InstanceConfigurationException(path, "solo se admite un documento YAML");
        }

        if (stream.Documents.Count == 1)
        {
            switch (stream.Documents[0].RootNode)
            {
                case YamlMappingNode root:
                    Flatten(root, InstanceOptions.SectionName, data);
                    break;
                case YamlScalarNode { Value: null or "" }:
                    break; // documento vacío
                default:
                    throw new InstanceConfigurationException(path, "la raíz debe ser un mapa de claves");
            }
        }

        Data = data;
    }

    private void Flatten(YamlNode node, string prefix, Dictionary<string, string?> data)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (keyNode, valueNode) in mapping.Children)
                {
                    if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key })
                    {
                        throw new InstanceConfigurationException(path, $"clave no válida (línea {keyNode.Start.Line})");
                    }

                    Flatten(valueNode, ConfigurationPath.Combine(prefix, key), data);
                }

                break;
            case YamlSequenceNode sequence:
                for (var i = 0; i < sequence.Children.Count; i++)
                {
                    Flatten(sequence.Children[i], ConfigurationPath.Combine(prefix, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), data);
                }

                break;
            case YamlScalarNode scalar:
                // Nulos YAML sin comillas (~, null, vacío) son ausencia de valor; "null" entre comillas es texto.
                data[prefix] = scalar.Style == ScalarStyle.Plain && PlainNulls.Contains(scalar.Value ?? string.Empty)
                    ? null
                    : scalar.Value;
                break;
            default:
                throw new InstanceConfigurationException(path, $"nodo YAML no admitido (línea {node.Start.Line})");
        }
    }
}

/// <summary>El archivo de configuración de instancia no es válido. El mensaje nombra la ruta, nunca el contenido.</summary>
internal sealed class InstanceConfigurationException(string path, string detail)
    : Exception($"El archivo de configuración de instancia '{path}' no es válido: {detail}.")
{
    public string ConfigPath { get; } = path;
}
