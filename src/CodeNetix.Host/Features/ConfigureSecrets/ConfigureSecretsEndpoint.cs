using CodeNetix.Host.Configuration;

namespace CodeNetix.Host.Features.ConfigureSecrets;

/// <summary>
/// Claves desde el Panel (historia 8.1), solo en <c>serve</c>, en <c>:5000</c> y con <c>admin-token</c> (AD-17: la guarda
/// de puertos ya devuelve 404 en <c>:5001</c> y <c>:8080</c>):
/// <list type="bullet">
/// <item><c>GET /api/settings/secrets</c>: salud y, por clave, si está configurada, su origen, si se puede editar y una
/// pista enmascarada. Nunca el valor.</item>
/// <item><c>PUT /api/settings/secrets</c>: comprueba y guarda las claves indicadas (todo o nada) y las aplica en caliente.
/// <c>200</c> con el estado nuevo; <c>400</c> sin claves o con un formato imposible; <c>409</c> si alguna la define el
/// entorno o el YAML; <c>422</c> si alguna no pasa la comprobación o empeoraría la configuración (con <c>errors</c> por
/// clave); <c>503</c> si la instancia está arrancando.</item>
/// </list>
/// </summary>
internal static class ConfigureSecretsEndpoint
{
    public const string Route = "/api/settings/secrets";

    public static IEndpointRouteBuilder MapConfigureSecrets(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, HandleGet)
            .WithName("GetSecretsSettings")
            .Produces<SecretsSettingsResponse>();

        endpoints.MapPut(Route, HandleUpdateAsync)
            .WithName("UpdateSecretsSettings")
            .Produces<SecretsSettingsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static IResult HandleGet(PanelSecretsService service, InstanceHealth health) =>
        Results.Ok(BuildResponse(service, health, []));

    private static async Task<IResult> HandleUpdateAsync(
        HttpContext context,
        UpdateSecretsRequest request,
        PanelSecretsService service,
        InstanceHealth health,
        CancellationToken cancellationToken)
    {
        var requested = new Dictionary<PanelSecretId, string?>
        {
            [PanelSecretId.GitHubToken] = request.GitHubToken,
            [PanelSecretId.AnthropicApiKey] = request.AnthropicApiKey,
            [PanelSecretId.OpenAIApiKey] = request.OpenAIApiKey,
        };

        var result = await service.UpdateAsync(requested, cancellationToken);
        if (result.Outcome == PanelSecretsUpdateOutcome.Saved)
        {
            var checks = result.Checks
                .OrderBy(c => c.Key)
                .Select(c => new SecretCheckResponse(c.Key, c.Value))
                .ToList();
            return Results.Ok(BuildResponse(service, health, checks));
        }

        var (status, title) = result.Outcome switch
        {
            PanelSecretsUpdateOutcome.Invalid => (StatusCodes.Status400BadRequest, "Petición no válida"),
            PanelSecretsUpdateOutcome.ManagedByConfiguration => (StatusCodes.Status409Conflict, "Clave gestionada fuera del Panel"),
            PanelSecretsUpdateOutcome.Rejected => (StatusCodes.Status422UnprocessableEntity, "No se ha guardado ninguna clave"),
            PanelSecretsUpdateOutcome.NotReady => (StatusCodes.Status503ServiceUnavailable, "CodeNetix está arrancando"),
            _ => (StatusCodes.Status500InternalServerError, "No se han podido guardar las claves"),
        };

        Dictionary<string, object?>? extensions = null;
        if (result.Errors.Count > 0)
        {
            // Claves en camelCase, como las propiedades de UpdateSecretsRequest.
            extensions = new Dictionary<string, object?>
            {
                ["errors"] = result.Errors.ToDictionary(e => FieldName(e.Key), e => e.Value, StringComparer.Ordinal),
            };
        }

        return Results.Problem(
            statusCode: status,
            title: title,
            detail: result.Errors.Count > 0
                ? result.Detail + " " + string.Join(" ", result.Errors.OrderBy(e => e.Key).Select(e => e.Value))
                : result.Detail,
            instance: context.Request.Path,
            extensions: extensions);
    }

    internal static string FieldName(PanelSecretId id) => id switch
    {
        PanelSecretId.GitHubToken => "gitHubToken",
        PanelSecretId.AnthropicApiKey => "anthropicApiKey",
        _ => "openAIApiKey",
    };

    private static SecretsSettingsResponse BuildResponse(PanelSecretsService service, InstanceHealth health, IReadOnlyList<SecretCheckResponse> checks)
    {
        var (status, reasons) = health.Read();
        var secrets = service.GetStates()
            .Select(s => new SecretSettingResponse(
                s.Id,
                s.Source != SecretSource.None,
                s.Source,
                s.Source is SecretSource.None or SecretSource.Panel,
                PanelSecretCatalog.EnvironmentVariable(s.Id),
                s.Hint))
            .ToList();
        return new SecretsSettingsResponse(status.ToString(), reasons, service.KeysMissing, secrets, service.Notice, checks);
    }
}

/// <summary>Claves que se quieren guardar; <c>null</c> o vacío = sin cambios.</summary>
internal sealed record UpdateSecretsRequest(string? GitHubToken, string? AnthropicApiKey, string? OpenAIApiKey);

/// <param name="Status"><c>Starting</c>, <c>Ready</c> o <c>Diagnostic</c> (como <c>/api/health</c>).</param>
/// <param name="KeysMissing">Falta el token de GitHub o la key de un Proveedor (motivo de <c>Diagnostic</c>).</param>
/// <param name="Notice">Aviso si el archivo de claves guardadas no se pudo leer, o <c>null</c>.</param>
/// <param name="Checks">Tras un <c>PUT</c>: el resultado de la comprobación de cada clave guardada.</param>
internal sealed record SecretsSettingsResponse(
    string Status,
    IReadOnlyList<string> Reasons,
    bool KeysMissing,
    IReadOnlyList<SecretSettingResponse> Secrets,
    string? Notice,
    IReadOnlyList<SecretCheckResponse> Checks);

/// <param name="Editable">Solo si no la define el entorno ni el archivo de instancia.</param>
/// <param name="EnvironmentVariable">Variable equivalente (para quien prefiera el entorno).</param>
/// <param name="Hint"><c>…</c> y los 4 últimos caracteres, o <c>null</c> sin valor. Nunca el valor.</param>
internal sealed record SecretSettingResponse(
    PanelSecretId Id,
    bool Configured,
    SecretSource Source,
    bool Editable,
    string EnvironmentVariable,
    string? Hint);

internal sealed record SecretCheckResponse(PanelSecretId Id, string Message);
