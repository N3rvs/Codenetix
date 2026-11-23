using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace CodeNetix.Sandbox.Docker;

/// <summary>
/// Separa el flujo multiplexado de Docker (<c>application/vnd.docker.multiplexed-stream</c>, exec sin TTY) en stdout y
/// stderr. Cada trama lleva una cabecera de 8 bytes: <c>[tipo, 0, 0, 0, tamaño (uint32 big-endian)]</c>, con tipo 1
/// (stdout) o 2 (stderr); el tipo 0 (stdin) se trata como stdout. El tipo 3 (error del propio motor, <c>systemerr</c>)
/// lanza <see cref="CodeNetix.Contracts.SandboxUnavailableException"/> con su texto; cualquier otro tipo se descarta.
/// La salida se acumula en los <see cref="BoundedOutput"/> del llamante (lo leído sobrevive a una cancelación); cada uno
/// recorta a su límite y descarta el resto sin dejar de leer (el proceso nunca se bloquea por una tubería llena).
/// </summary>
internal static class MultiplexedStreamReader
{
    private const int MaxSystemErrorBytes = 4096;

    public static async Task ReadAsync(Stream stream, BoundedOutput stdout, BoundedOutput stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var discard = new BoundedOutput(0);
        BoundedOutput? systemError = null;
        var header = new byte[8];
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                var target = header[0] switch
                {
                    0 or 1 => stdout,
                    2 => stderr,
                    3 => systemError ??= new BoundedOutput(MaxSystemErrorBytes),
                    _ => discard,
                };
                var remaining = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(remaining, (uint)buffer.Length);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        // Trama cortada (el contenedor se ha detenido): se conserva lo leído.
                        ThrowIfSystemError(systemError);
                        return;
                    }

                    target.Append(buffer.AsSpan(0, read));
                    remaining -= (uint)read;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        ThrowIfSystemError(systemError);
    }

    private static void ThrowIfSystemError(BoundedOutput? systemError)
    {
        if (systemError is not null)
        {
            throw new CodeNetix.Contracts.SandboxUnavailableException($"Docker ha devuelto un error en el flujo del comando: {systemError.ToString().Trim()}");
        }
    }

    /// <summary><c>false</c> si el flujo termina antes del primer byte; una cabecera cortada también cuenta como fin.</summary>
    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] header, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}

/// <summary>Salida acumulada hasta un límite de bytes; lo que lo supera se descarta y marca <see cref="Truncated"/>.</summary>
internal sealed class BoundedOutput(int limit)
{
    private readonly MemoryStream _buffer = new();

    public bool Truncated { get; private set; }

    public long TotalBytes { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        TotalBytes += data.Length;
        var room = limit - (int)_buffer.Length;
        if (data.Length > room)
        {
            Truncated = true;
            data = data[..Math.Max(room, 0)];
        }

        _buffer.Write(data);
    }

    /// <summary>Texto UTF-8; si el recorte ha partido un carácter multibyte al final, se omite ese resto.</summary>
    public override string ToString()
    {
        var bytes = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
        if (Truncated)
        {
            bytes = bytes[..CompleteUtf8Length(bytes)];
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Longitud sin una secuencia UTF-8 incompleta al final.</summary>
    private static int CompleteUtf8Length(ReadOnlySpan<byte> bytes)
    {
        // Retrocede hasta el inicio del último carácter (como mucho 3 bytes de continuación).
        var i = bytes.Length - 1;
        var continuation = 0;
        while (i >= 0 && continuation < 3 && (bytes[i] & 0xC0) == 0x80)
        {
            i--;
            continuation++;
        }

        if (i < 0)
        {
            return bytes.Length;
        }

        var lead = bytes[i];
        var expected = lead < 0x80 ? 1 : (lead & 0xE0) == 0xC0 ? 2 : (lead & 0xF0) == 0xE0 ? 3 : (lead & 0xF8) == 0xF0 ? 4 : 1;
        return continuation + 1 >= expected ? bytes.Length : i;
    }
}
