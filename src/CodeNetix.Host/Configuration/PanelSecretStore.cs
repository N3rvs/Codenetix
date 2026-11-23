using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeNetix.Host.Secrets;
using CodeNetix.Persistence;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Configuration;

/// <summary>Claves que se pueden configurar desde el Panel (historia 8.1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PanelSecretId>))]
internal enum PanelSecretId
{
    GitHubToken,
    AnthropicApiKey,
    OpenAIApiKey,
}

/// <summary>De dónde sale el valor efectivo de una clave.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SecretSource>))]
internal enum SecretSource
{
    /// <summary>Sin valor.</summary>
    None,

    /// <summary>Variables <c>CODENETIX__*</c> (o línea de comandos): solo lectura en el Panel.</summary>
    Environment,

    /// <summary>Archivo de instancia <c>codenetix.instance.yml</c>: solo lectura en el Panel.</summary>
    ConfigFile,

    /// <summary>Guardada (cifrada) desde el Panel.</summary>
    Panel,
}

/// <summary>Metadatos de cada clave gestionable: clave de configuración, variable de entorno y Proveedor.</summary>
internal static class PanelSecretCatalog
{
    public static readonly IReadOnlyList<PanelSecretId> All = [PanelSecretId.GitHubToken, PanelSecretId.AnthropicApiKey, PanelSecretId.OpenAIApiKey];

    /// <summary>Clave de configuración (<c>CODENETIX:…</c>).</summary>
    public static string ConfigurationKey(PanelSecretId id) => id switch
    {
        PanelSecretId.GitHubToken => GitHubInstanceOptions.SectionName + ":Token",
        _ => $"{InstanceOptions.SectionName}:Providers:{ProviderName(id)}:ApiKey",
    };

    /// <summary>Variable de entorno equivalente (<c>CODENETIX__…</c>).</summary>
    public static string EnvironmentVariable(PanelSecretId id) => ConfigurationKey(id).Replace(":", "__", StringComparison.Ordinal);

    /// <summary>Nombre del Proveedor de una key; <c>null</c> para el token de GitHub.</summary>
    public static string? ProviderName(PanelSecretId id) => id switch
    {
        PanelSecretId.AnthropicApiKey => AgentEndpointResolver.Anthropic,
        PanelSecretId.OpenAIApiKey => AgentEndpointResolver.OpenAI,
        _ => null,
    };

    /// <summary>Nombre para mensajes y logs (nunca el valor).</summary>
    public static string Label(PanelSecretId id) => id switch
    {
        PanelSecretId.GitHubToken => "token de GitHub",
        PanelSecretId.AnthropicApiKey => "key de Anthropic",
        _ => "key de OpenAI",
    };

    /// <summary>
    /// Origen del valor efectivo de <paramref name="id"/>: el mismo proveedor de configuración que ganaría en
    /// <c>configuration[clave]</c> (el último que la define). Un valor vacío o en blanco cuenta como ausente: la
    /// clave del Panel puede rellenarlo.
    /// </summary>
    public static SecretSource SourceOf(IConfiguration configuration, PanelSecretId id, PanelSecrets panel)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(panel);

        var key = ConfigurationKey(id);
        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers.Reverse())
            {
                if (provider.TryGet(key, out var value))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return provider is YamlInstanceConfigurationProvider ? SecretSource.ConfigFile : SecretSource.Environment;
                    }

                    break;
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(configuration[key]))
        {
            return SecretSource.Environment;
        }

        return panel.Get(id) is null ? SecretSource.None : SecretSource.Panel;
    }

    /// <summary><c>true</c> si la configuración (entorno o YAML) ya da valor a la clave: el Panel no la toca.</summary>
    public static bool IsSetByConfiguration(IConfiguration configuration, PanelSecretId id) =>
        !string.IsNullOrWhiteSpace(configuration[ConfigurationKey(id)]);

    /// <summary>
    /// Superpone las claves del Panel sobre la Configuración de instancia, solo donde el entorno y el YAML no dan valor
    /// (el entorno y el archivo tienen prioridad, AD-10).
    /// </summary>
    public static void Apply(InstanceOptions instance, PanelSecrets panel, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var id in All)
        {
            if (panel.Get(id) is not { } value || IsSetByConfiguration(configuration, id))
            {
                continue;
            }

            if (ProviderName(id) is { } providerName)
            {
                instance.Providers ??= new(StringComparer.OrdinalIgnoreCase);
                if (!instance.Providers.TryGetValue(providerName, out var provider) || provider is null)
                {
                    provider = new ProviderInstanceOptions();
                    instance.Providers[providerName] = provider;
                }

                provider.ApiKey = value;
            }
            else
            {
                instance.GitHub ??= new GitHubInstanceOptions();
                instance.GitHub.Token = value;
            }
        }
    }

    /// <summary>
    /// Pista para el Panel: <c>…</c> y los 4 últimos caracteres (solo con 12 o más; si no, <c>…</c>). Nunca más:
    /// el prefijo (<c>ghp_</c>, <c>sk-ant-</c>) no aporta y acorta lo que queda por adivinar.
    /// </summary>
    public static string Hint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        return trimmed.Length >= 12
            ? string.Concat(SecretRedactor.Ellipsis, trimmed.AsSpan(trimmed.Length - 4))
            : SecretRedactor.Ellipsis;
    }
}

/// <summary>Claves guardadas desde el Panel (valores recortados; nunca se registran).</summary>
internal sealed class PanelSecrets
{
    public static readonly PanelSecrets Empty = new(new Dictionary<PanelSecretId, string>());

    private readonly Dictionary<PanelSecretId, string> _values;

    public PanelSecrets(IReadOnlyDictionary<PanelSecretId, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value.Trim());
    }

    public string? Get(PanelSecretId id) => _values.TryGetValue(id, out var value) ? value : null;

    public IEnumerable<string> Values => _values.Values;

    public IReadOnlyDictionary<PanelSecretId, string> AsDictionary() => _values;

    /// <summary>Copia con <paramref name="changes"/> aplicados encima.</summary>
    public PanelSecrets With(IReadOnlyDictionary<PanelSecretId, string> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var merged = new Dictionary<PanelSecretId, string>(_values);
        foreach (var (id, value) in changes)
        {
            merged[id] = value;
        }

        return new PanelSecrets(merged);
    }

    /// <summary>Sin los valores: un <c>ToString</c> por defecto nunca debe poder imprimirlos.</summary>
    public override string ToString() => $"PanelSecrets({string.Join(", ", _values.Keys)})";
}

/// <summary>
/// Almacén cifrado de las claves del Panel (historia 8.1) en el directorio de datos:
/// <list type="bullet">
/// <item><c>panel-secrets.key</c>: 32 bytes aleatorios (base64), creado una vez con permisos 0600 fuera de Windows.</item>
/// <item><c>panel-secrets.enc</c>: JSON <c>{version, nonce, tag, ciphertext}</c> con AES-256-GCM y datos asociados fijos
/// (detecta cualquier manipulación). Se reescribe de forma atómica (temporal 0600 + <c>Move</c>).</item>
/// </list>
/// Se carga de forma perezosa en el primer acceso (las opciones pueden pedirla antes que el arranque ordenado). Un archivo
/// ilegible no bloquea la instancia: se arranca sin esas claves y <see cref="LoadError"/> lo explica. Sin logger a
/// propósito: el redactor depende de este almacén y el registro, del redactor.
/// </summary>
internal sealed class PanelSecretStore
{
    public const string FileName = "panel-secrets.enc";
    public const string KeyFileName = "panel-secrets.key";
    public const int FormatVersion = 1;

    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] AssociatedData = "codenetix.panel-secrets.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _lock = new();
    private volatile State? _state;

    public PanelSecretStore(IOptions<PersistenceOptions> persistenceOptions)
    {
        ArgumentNullException.ThrowIfNull(persistenceOptions);
        var dataDirectory = persistenceOptions.Value.DataDirectory;
        var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(dataDirectory) ? PersistenceOptions.DefaultDataDirectory : dataDirectory);
        FilePath = Path.Combine(directory, FileName);
        KeyPath = Path.Combine(directory, KeyFileName);
    }

    /// <summary>Ruta del archivo cifrado (se puede registrar; el contenido no).</summary>
    public string FilePath { get; }

    /// <summary>Ruta de la clave de cifrado.</summary>
    public string KeyPath { get; }

    /// <summary>Claves vigentes (vacío si no hay archivo o no se pudo leer).</summary>
    public PanelSecrets Current => EnsureLoaded().Secrets;

    /// <summary>Por qué no se pudo leer el archivo guardado (sin valores), o <c>null</c>.</summary>
    public string? LoadError => EnsureLoaded().Error;

    /// <summary>
    /// Cifra y guarda <paramref name="secrets"/> (sustituye el archivo entero). Lanza <see cref="IOException"/> o
    /// <see cref="UnauthorizedAccessException"/> si no se puede escribir; en ese caso <see cref="Current"/> no cambia.
    /// </summary>
    public void Save(PanelSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        lock (_lock)
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);

            // Una clave ilegible solo protegía un archivo que ya no se puede leer: se sustituye.
            byte[]? existingKey;
            try
            {
                existingKey = ReadKey();
            }
            catch (FormatException)
            {
                existingKey = null;
            }

            var key = existingKey ?? CreateKey();
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(
                new Payload(secrets.AsDictionary().ToDictionary(p => p.Key.ToString(), p => p.Value, StringComparer.Ordinal)),
                Json);
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[TagSize];
                using (var aes = new AesGcm(key, TagSize))
                {
                    aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
                }

                var envelope = JsonSerializer.SerializeToUtf8Bytes(
                    new Envelope(FormatVersion, Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext)),
                    Json);
                WriteAtomically(FilePath, envelope, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(key);
            }

            _state = new State(secrets, null);
        }
    }

    private State EnsureLoaded()
    {
        if (_state is { } loaded)
        {
            return loaded;
        }

        lock (_lock)
        {
            return _state ??= Load();
        }
    }

    private State Load()
    {
        if (!File.Exists(FilePath))
        {
            return new State(PanelSecrets.Empty, null);
        }

        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            key = ReadKey();
            if (key is null)
            {
                return Unreadable($"falta la clave de cifrado '{KeyPath}'");
            }

            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(FilePath), Json);
            if (envelope is not { Version: FormatVersion, Nonce: { } nonceText, Tag: { } tagText, Ciphertext: { } ciphertextText })
            {
                return Unreadable("formato no reconocido");
            }

            var nonce = Convert.FromBase64String(nonceText);
            var tag = Convert.FromBase64String(tagText);
            var ciphertext = Convert.FromBase64String(ciphertextText);
            if (nonce.Length != NonceSize || tag.Length != TagSize)
            {
                return Unreadable("formato no reconocido");
            }

            plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            }

            var payload = JsonSerializer.Deserialize<Payload>(plaintext, Json);
            var values = new Dictionary<PanelSecretId, string>();
            foreach (var (name, value) in payload?.Secrets ?? [])
            {
                if (Enum.TryParse<PanelSecretId>(name, ignoreCase: false, out var id) && Enum.IsDefined(id) && !string.IsNullOrWhiteSpace(value))
                {
                    values[id] = value;
                }
            }

            return new State(new PanelSecrets(values), null);
        }
        catch (AuthenticationTagMismatchException)
        {
            return Unreadable("el contenido no se puede descifrar con la clave actual (¿manipulado o de otra instalación?)");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            return Unreadable("formato no reconocido");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable("no se puede leer el archivo");
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private State Unreadable(string detail) => new(
        PanelSecrets.Empty,
        $"No se pueden leer las claves guardadas desde el Panel ('{FilePath}': {detail}). CodeNetix arranca sin ellas: vuelve a introducirlas en Configuración.");

    private byte[]? ReadKey()
    {
        if (!File.Exists(KeyPath))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (File.GetUnixFileMode(KeyPath) != OwnerOnly)
            {
                File.SetUnixFileMode(KeyPath, OwnerOnly);
            }
        }

        var key = Convert.FromBase64String(File.ReadAllText(KeyPath, Encoding.ASCII).Trim());
        if (key.Length != KeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new FormatException("La clave de cifrado no tiene 32 bytes.");
        }

        return key;
    }

    private byte[] CreateKey()
    {
        var key = RandomNumberGenerator.GetBytes(KeySize);
        WriteAtomically(KeyPath, Encoding.ASCII.GetBytes(Convert.ToBase64String(key)), overwrite: true);
        return key;
    }

    /// <summary>Temporal con 0600 desde su creación y <c>Move</c>: nunca hay un archivo a medio escribir ni más abierto.</summary>
    private static void WriteAtomically(string path, byte[] content, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path)!;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(tempPath, options))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Mejor esfuerzo.
            }
        }
    }

    private sealed record State(PanelSecrets Secrets, string? Error);

    private sealed record Envelope(int Version, string? Nonce, string? Tag, string? Ciphertext);

    private sealed record Payload(Dictionary<string, string>? Secrets);
}
