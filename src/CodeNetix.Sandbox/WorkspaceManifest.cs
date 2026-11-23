using System.Security.Cryptography;
using System.Text;

namespace CodeNetix.Sandbox;

/// <summary>
/// Huella del árbol de <c>/workspace</c> sin <c>.git</c> (AD-6): un registro por archivo regular
/// (<c>"&lt;sha256&gt;  ./&lt;ruta&gt;\0"</c>, el formato de <c>sha256sum -z</c>) y por enlace simbólico
/// (<c>"L &lt;destino&gt; ./&lt;ruta&gt;\0"</c>), ordenados por bytes (como <c>LC_ALL=C sort -z</c>) y resumidos con
/// SHA-256. Se calcula al subir el tar del <c>BaseSha</c> y se compara con la que <see cref="Script"/> calcula dentro
/// del contenedor, así que ambos lados deben producir exactamente los mismos bytes.
/// </summary>
internal sealed class WorkspaceManifest
{
    /// <summary>
    /// Script de <c>sh -c</c> (en <c>/workspace</c>) que imprime <c>"&lt;sha256&gt;  -"</c> con la huella del árbol actual.
    /// </summary>
    public const string Script =
        "export LC_ALL=C; set -e; { find . -mindepth 1 -type f -exec sha256sum -z -- {} + ; "
        + "find . -mindepth 1 -type l -printf 'L %l %p\\0'; } | sort -z | sha256sum";

    private readonly List<byte[]> _records = [];

    /// <summary>Añade un archivo regular con ruta relativa <paramref name="path"/> (segmentos unidos por <c>/</c>).</summary>
    public void AddFile(string path, byte[] sha256) =>
        _records.Add(Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(sha256)}  ./{path}\0"));

    /// <summary>Añade un enlace simbólico con ruta relativa <paramref name="path"/> y destino <paramref name="target"/>.</summary>
    public void AddSymlink(string path, string target) =>
        _records.Add(Encoding.UTF8.GetBytes($"L {target} ./{path}\0"));

    /// <summary>SHA-256 en hexadecimal (minúsculas) de los registros ordenados por bytes.</summary>
    public string ComputeDigest()
    {
        var sorted = _records.ToList();
        sorted.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var record in sorted)
        {
            hash.AppendData(record);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Extrae la huella de la salida de <c>sha256sum</c> (<c>"&lt;hex&gt;  -"</c>); <c>null</c> si no la reconoce.</summary>
    public static string? ParseDigest(string output)
    {
        var trimmed = output.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var hex = space < 0 ? trimmed : trimmed[..space];
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigitLower) ? hex : null;
    }
}

