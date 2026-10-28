using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CodeNetix.Agents.StructuredOutput;

/// <summary>Petición de salida estructurada: mensajes fijos más el modo del Proveedor.</summary>
/// <param name="SystemPrompt">Fijo; nunca contiene texto no confiable.</param>
/// <param name="UserPrompt">Donde van los bloques <c>UntrustedText.RenderAsDataBlock</c>.</param>
/// <param name="StructuredOutput">
/// <c>true</c>: <c>response_format: json_schema</c>. <c>false</c>: el esquema va en el prompt de sistema y se extrae
/// el primer objeto JSON de la respuesta (se toleran vallas <c>```json</c>).
/// </param>
internal sealed record StructuredCall(string SystemPrompt, string UserPrompt, bool StructuredOutput);

/// <summary>Resultado de <see cref="SchemaRetryDecorator.RunAsync"/>.</summary>
internal abstract record StructuredOutcome<T>
    where T : class
{
    private StructuredOutcome()
    {
    }

    public sealed record Success(T Value, int Attempts) : StructuredOutcome<T>;

    public sealed record Invalid(string LastError, int Attempts) : StructuredOutcome<T>;

    public sealed record ProviderFailed(string Detail) : StructuredOutcome<T>;
}

/// <summary>
/// Decorador común de reintentos de esquema (AD-5), reutilizable por cualquier agente con salida estructurada:
/// <list type="bullet">
/// <item>Intento 1 + <see cref="AgentsOptions.SchemaRetries"/> reintentos; cada respuesta se valida contra el esquema y
/// las reglas semánticas de <see cref="StructuredOutputValidator{T}"/>.</item>
/// <item>Cada rechazo se notifica (<c>onRejected</c>) y, si quedan reintentos, se añade al diálogo un mensaje con el
/// error de validación (nunca la respuesta anterior ni el texto de la issue).</item>
/// <item>Un error HTTP, de red o un timeout (<see cref="AgentsOptions.CallTimeout"/> por llamada) termina con
/// <see cref="StructuredOutcome{T}.ProviderFailed"/> sin consumir reintentos.</item>
/// <item>Sin herramientas: nunca se envían <c>tools</c> ni se ejecuta nada.</item>
/// </list>
/// </summary>
internal sealed class SchemaRetryDecorator(IOptions<AgentsOptions> options)
{
    public const int MaxDetailLength = 300;

    /// <summary>Tope de tokens de salida por llamada (una Spec cabe con holgura).</summary>
    public const int MaxOutputTokens = 8192;

    public async Task<StructuredOutcome<T>> RunAsync<T>(
        IChatClient client,
        string model,
        StructuredCall call,
        StructuredOutputValidator<T> validator,
        Func<int, string, CancellationToken, Task> onRejected,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(onRejected);

        var settings = options.Value;
        var retries = Math.Clamp(settings.SchemaRetries, 0, AgentsOptions.MaxSchemaRetries);
        var timeout = settings.CallTimeout > TimeSpan.Zero ? settings.CallTimeout : AgentsOptions.DefaultCallTimeout;

        var systemPrompt = call.StructuredOutput ? call.SystemPrompt : call.SystemPrompt + "\n\n" + SchemaInPrompt(validator.PromptSchemaJson);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, call.UserPrompt),
        };

        var chatOptions = new ChatOptions
        {
            ModelId = model,
            MaxOutputTokens = MaxOutputTokens,
            ResponseFormat = call.StructuredOutput
                ? ChatResponseFormat.ForJsonSchema(validator.RequestSchema, validator.SchemaName)
                : null,
        };

        var lastError = string.Empty;
        var totalAttempts = retries + 1;
        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            ChatResponse response;
            using (var callTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                callTimeout.CancelAfter(timeout);
                try
                {
                    response = await client.GetResponseAsync(messages, chatOptions, callTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new StructuredOutcome<T>.ProviderFailed(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sin respuesta en {timeout.TotalSeconds:0.###} s"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Un cliente que envuelve la cancelación en otra excepción no la convierte en fallo del Proveedor.
                    cancellationToken.ThrowIfCancellationRequested();
                    return new StructuredOutcome<T>.ProviderFailed(DescribeProviderError(ex));
                }
            }

            var validation = validator.Validate(response.Text);
            if (validation.Value is { } value)
            {
                return new StructuredOutcome<T>.Success(value, attempt);
            }

            lastError = validation.Error ?? "respuesta no válida";
            await onRejected(attempt, lastError, cancellationToken).ConfigureAwait(false);

            if (attempt < totalAttempts)
            {
                messages.Add(new ChatMessage(ChatRole.User, RetryPrompt(lastError)));
            }
        }

        return new StructuredOutcome<T>.Invalid(lastError, totalAttempts);
    }

    /// <summary>Con <c>StructuredOutput=false</c>: el esquema va en el prompt de sistema (texto fijo).</summary>
    internal static string SchemaInPrompt(string schemaJson) =>
        "Your answer must be a single JSON object that conforms to this JSON Schema (draft 2020-12). "
        + "Reply with the JSON object only, optionally inside a ```json fence, with no other text:\n"
        + "```json\n" + schemaJson.Trim() + "\n```";

    /// <summary>Mensaje de corrección: solo el error de validación.</summary>
    internal static string RetryPrompt(string error) =>
        "Your previous answer was rejected by validation: " + error + "\n"
        + "Answer again with a single JSON object that conforms to the schema and fixes this error.";

    /// <summary>Detalle de un error del Proveedor en una línea: estado HTTP y, si lo hay, el mensaje de error del cuerpo.</summary>
    internal static string DescribeProviderError(Exception error)
    {
        string detail;
        if (error is ClientResultException result)
        {
            detail = result.Status > 0 ? $"HTTP {result.Status}" : "error de red";
            if (ProviderMessage(result) is { } message)
            {
                detail += ": " + message;
            }
            else if (result.Status == 0)
            {
                detail += ": " + error.Message;
            }
        }
        else if (error is HttpRequestException http)
        {
            detail = http.StatusCode is { } status ? $"HTTP {(int)status}" : "error de red: " + error.Message;
        }
        else
        {
            detail = error.Message;
        }

        detail = string.Join(' ', detail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return detail.Length <= MaxDetailLength ? detail : string.Concat(detail.AsSpan(0, MaxDetailLength - 1), "…");
    }

    /// <summary><c>{"error": {"message": "…"}}</c> (OpenAI y compatibles) o <c>{"error": "…"}</c>.</summary>
    private static string? ProviderMessage(ClientResultException error)
    {
        try
        {
            var content = error.GetRawResponse()?.Content;
            if (content is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(content.ToMemory());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var errorElement))
            {
                return null;
            }

            if (errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString();
            }

            return errorElement.ValueKind == JsonValueKind.Object
                && errorElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }
}
