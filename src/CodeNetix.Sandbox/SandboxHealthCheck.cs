using CodeNetix.Sandbox.Docker;
using Microsoft.Extensions.Options;

namespace CodeNetix.Sandbox;

/// <summary>
/// Comprobación de arranque del Sandbox (AD-18 paso 2): Host la invoca y, si hay motivos, la instancia queda en
/// <c>Diagnostic</c>.
/// </summary>
public interface ISandboxHealthCheck
{
    /// <summary>Motivos (en español, sin secretos) por los que no se puede crear un Sandbox; vacío si todo está bien.</summary>
    Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Docker responde a <c>/_ping</c>, ofrece la API v1.43 o superior y tiene la imagen del Sandbox. Cada llamada a
/// Docker tiene un plazo corto para no retrasar el arranque.
/// </summary>
internal sealed class SandboxHealthCheck(DockerClient docker, IOptions<SandboxOptions> options) : ISandboxHealthCheck
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    public static string DockerUnreachable(string endpoint, string reason) =>
        $"Docker no responde en {endpoint} ({reason}): el Sandbox necesita el motor Docker del host. En contenedor, monta su socket con -v /var/run/docker.sock:/var/run/docker.sock y dale acceso con --group-add <grupo del socket> (o fija CODENETIX__Sandbox__DockerEndpoint).";

    public static string UnsupportedEngine(string endpoint, string reason) =>
        $"El motor Docker de {endpoint} no es compatible ({reason}): actualiza Docker a la versión 24 o superior.";

    public static string MissingImage(string image) =>
        $"Falta la imagen {image} del Sandbox: constrúyela con {SandboxManager.BuildHint}";

    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken)
    {
        var image = string.IsNullOrWhiteSpace(options.Value.Image) ? SandboxOptions.DefaultImage : options.Value.Image.Trim();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            await docker.PingAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
        {
            return [DockerUnreachable(docker.Endpoint.ToString(), OneLine(ex))];
        }

        try
        {
            await docker.GetApiPrefixAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (ex.StatusCode == 0)
        {
            return [UnsupportedEngine(docker.Endpoint.ToString(), OneLine(ex))];
        }
        catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
        {
            return [DockerUnreachable(docker.Endpoint.ToString(), OneLine(ex))];
        }

        try
        {
            if (!await docker.ImageExistsAsync(image, timeout.Token).ConfigureAwait(false))
            {
                return [MissingImage(image)];
            }
        }
        catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
        {
            return [DockerUnreachable(docker.Endpoint.ToString(), OneLine(ex))];
        }

        return [];
    }

    /// <summary>
    /// Cualquier fallo salvo la cancelación del arranque (también una respuesta que no es JSON o un error inesperado):
    /// la comprobación nunca hace caer el arranque, solo lleva a <c>Diagnostic</c>.
    /// </summary>
    private static bool IsUnreachable(Exception ex, CancellationToken cancellationToken) =>
        ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    private static string OneLine(Exception ex)
    {
        var message = ex switch
        {
            OperationCanceledException => "sin respuesta a tiempo",

            // HttpClient añade "(host:puerto)" ficticio del transporte: el motivo útil es el de la conexión.
            HttpRequestException { InnerException: IOException inner } => inner.Message,
            System.Text.Json.JsonException => "respuesta no válida de la API de Docker",
            _ => ex.Message,
        };
        message = string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        return message.Length <= 200 ? message : string.Concat(message.AsSpan(0, 199), "…");
    }
}
