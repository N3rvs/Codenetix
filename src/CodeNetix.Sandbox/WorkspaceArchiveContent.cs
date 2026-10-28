using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;

namespace CodeNetix.Sandbox;

/// <summary>
/// Cuerpo de <c>PUT /containers/{id}/archive</c>: reescribe al vuelo el tar de <c>git archive</c> para subirlo a
/// <c>/workspace</c> sin que nada escape de esa raíz:
/// <list type="bullet">
/// <item>primero una entrada <c>./</c> (directorio 0755 de 1000:1000), para que la raíz del volumen sea del usuario
/// <c>sandbox</c>;</item>
/// <item>todas las entradas pasan a ser de 1000:1000 (<c>git archive</c> las marca como root);</item>
/// <item>se descartan la cabecera global PAX, las rutas absolutas o con <c>..</c>, cualquier entrada bajo <c>.git</c> o
/// bajo un enlace simbólico ya visto, los enlaces simbólicos absolutos o que salen de la raíz y los tipos especiales
/// (dispositivos, FIFO);</item>
/// <item>un enlace duro (que <c>git archive</c> nunca produce) hace fallar la subida con <see cref="InvalidDataException"/>.</item>
/// </list>
/// </summary>
internal sealed class WorkspaceArchiveContent : HttpContent
{
    public const int SandboxUid = 1000;

    public const int SandboxGid = 1000;

    private const UnixFileMode RootMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly Stream _source;

    public WorkspaceArchiveContent(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        _source.Position = 0;
        var reader = new TarReader(_source, leaveOpen: true);
        await using (reader.ConfigureAwait(false))
        {
            var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteEntryAsync(Owned(new PaxTarEntry(TarEntryType.Directory, "./") { Mode = RootMode }), cancellationToken).ConfigureAwait(false);

                var symlinks = new List<string[]>();
                while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
                {
                    if (entry.EntryType == TarEntryType.HardLink)
                    {
                        throw new InvalidDataException($"el tar contiene un enlace duro ('{entry.Name}'), que git archive nunca produce");
                    }

                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory or TarEntryType.SymbolicLink)
                        || SegmentsOf(entry.Name) is not { Length: > 0 } segments
                        || segments.Contains(".git")
                        || symlinks.Any(link => IsUnder(segments, link)))
                    {
                        continue;
                    }

                    if (entry.EntryType == TarEntryType.SymbolicLink)
                    {
                        // Lo que haya bajo el enlace se escribiría a través de él: se descarta en cualquier caso.
                        symlinks.Add(segments);
                        if (!IsContainedLink(segments, entry.LinkName))
                        {
                            continue;
                        }
                    }

                    var copy = Owned(new PaxTarEntry(entry));
                    await writer.WriteEntryAsync(copy, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    /// <summary>¿Algún segmento de la ruta es <c>.git</c>?</summary>
    internal static bool IsUnderGit(string name) =>
        name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".git");

    /// <summary>
    /// Segmentos de una ruta relativa (sin <c>.</c>); <c>null</c> si es absoluta o tiene <c>..</c>. Vacío para la raíz.
    /// </summary>
    internal static string[]? SegmentsOf(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/'))
        {
            return null;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();
        return segments.Contains("..") ? null : segments;
    }

    /// <summary>¿El destino relativo del enlace en <paramref name="link"/> se queda dentro de la raíz?</summary>
    internal static bool IsContainedLink(string[] link, string target)
    {
        if (string.IsNullOrEmpty(target) || target.Replace('\\', '/').StartsWith('/'))
        {
            return false;
        }

        // Se resuelve desde el directorio del enlace; la profundidad nunca puede bajar de la raíz.
        var depth = link.Length - 1;
        foreach (var segment in target.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (--depth < 0)
                {
                    return false;
                }
            }
            else if (segment != ".")
            {
                depth++;
            }
        }

        return true;
    }

    private static bool IsUnder(string[] path, string[] ancestor) =>
        path.Length > ancestor.Length && path.AsSpan(0, ancestor.Length).SequenceEqual(ancestor);

    private static PaxTarEntry Owned(PaxTarEntry entry)
    {
        entry.Uid = SandboxUid;
        entry.Gid = SandboxGid;
        entry.UserName = "sandbox";
        entry.GroupName = "sandbox";
        return entry;
    }
}
