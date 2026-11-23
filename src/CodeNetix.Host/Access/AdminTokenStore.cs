using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using CodeNetix.Host.Configuration;
using CodeNetix.Persistence;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Access;

/// <summary>
/// <c>admin-token</c> de la instancia (AD-17). El token efectivo es <c>CODENETIX__Host__AccessToken</c>
/// si tiene valor; si no, el de <c>&lt;DataDirectory&gt;/admin.token</c>. El archivo lo asegura
/// <see cref="StartupReconciler"/> en cada arranque (aunque haya <c>AccessToken</c>): se genera si
/// falta (permisos 0600 fuera de Windows) y, si existe, se reutiliza sin reescribirlo.
/// El valor nunca se registra.
/// </summary>
internal sealed class AdminTokenStore
{
    public const string FileName = "admin.token";
    public const string TokenPrefix = "cnx_";
    public const int RandomByteCount = 32;

    private readonly string? _configuredToken;
    private volatile string? _fileToken;

    public AdminTokenStore(IOptions<InstanceOptions> instanceOptions, IOptions<PersistenceOptions> persistenceOptions)
    {
        ArgumentNullException.ThrowIfNull(instanceOptions);
        ArgumentNullException.ThrowIfNull(persistenceOptions);

        var configured = instanceOptions.Value.Host?.AccessToken;
        _configuredToken = string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();

        var dataDirectory = persistenceOptions.Value.DataDirectory;
        FilePath = Path.Combine(
            Path.GetFullPath(string.IsNullOrWhiteSpace(dataDirectory) ? PersistenceOptions.DefaultDataDirectory : dataDirectory),
            FileName);
    }

    /// <summary>Ruta de <c>admin.token</c> (se puede registrar; el contenido no).</summary>
    public string FilePath { get; }

    /// <summary><c>true</c> si el token efectivo es <c>CODENETIX__Host__AccessToken</c>.</summary>
    public bool UsesConfiguredToken => _configuredToken is not null;

    /// <summary>
    /// Token con el que se autentica <c>/api</c> y <c>/hubs</c>, o <c>null</c> si aún no se ha cargado
    /// el archivo (en ese caso se deniega todo).
    /// </summary>
    public string? EffectiveToken => _configuredToken ?? _fileToken;

    /// <summary>
    /// Para el cliente <c>codenetix fix</c>: el token efectivo (<c>AccessToken</c> o el contenido de
    /// <c>admin.token</c>) sin crear ni modificar el archivo. <c>null</c> si no hay <c>AccessToken</c> y el
    /// archivo no existe; lanza <see cref="InvalidOperationException"/> si está vacío.
    /// </summary>
    public string? ReadEffectiveTokenReadOnly()
    {
        if (_configuredToken is not null)
        {
            return _configuredToken;
        }

        return TryRead(out var token, fixPermissions: false) ? token : null;
    }

    /// <summary>Crea <c>admin.token</c> si no existe o lo lee si ya existe.</summary>
    public void EnsureTokenFile()
    {
        if (TryRead(out var existing, fixPermissions: true))
        {
            _fileToken = existing;
            return;
        }

        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);

        // Se escribe en un temporal con 0600 desde su creación y se mueve sin sobrescribir:
        // nunca hay un admin.token a medio escribir ni con permisos más abiertos.
        var token = Generate();
        var tempPath = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
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
                stream.Write(Encoding.ASCII.GetBytes(token));
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tempPath, FilePath, overwrite: false);
                _fileToken = token;
            }
            catch (IOException) when (File.Exists(FilePath))
            {
                // Otro proceso lo creó a la vez: gana el suyo.
                if (!TryRead(out existing, fixPermissions: true))
                {
                    throw;
                }

                _fileToken = existing;
            }
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

    /// <summary><c>cnx_</c> + 32 bytes aleatorios criptográficos en base64url (sin relleno).</summary>
    public static string Generate() =>
        TokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomByteCount));

    private bool TryRead(out string token, bool fixPermissions)
    {
        token = string.Empty;
        if (!File.Exists(FilePath))
        {
            return false;
        }

        var content = File.ReadAllText(FilePath, Encoding.UTF8).Trim();
        if (content.Length == 0)
        {
            throw new InvalidOperationException(
                $"El archivo del admin-token '{FilePath}' está vacío. Bórralo para que CodeNetix genere uno nuevo.");
        }

        // Se reutiliza sin reescribir el contenido, pero nunca con permisos más abiertos que 0600.
        if (fixPermissions && !OperatingSystem.IsWindows())
        {
            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (File.GetUnixFileMode(FilePath) != OwnerOnly)
            {
                File.SetUnixFileMode(FilePath, OwnerOnly);
            }
        }

        token = content;
        return true;
    }
}
