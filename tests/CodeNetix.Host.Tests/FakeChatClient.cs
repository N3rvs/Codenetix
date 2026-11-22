using System.Collections.Concurrent;
using CodeNetix.Agents;
using CodeNetix.Agents.ChatClients;
using CodeNetix.Contracts;
using Microsoft.Extensions.AI;

namespace CodeNetix.Host.Tests;

/// <summary>
/// <see cref="IChatClient"/> falso con un guion de respuestas: cada llamada consume el siguiente paso del guion
/// (texto, excepción o espera) y, sin guion, responde <see cref="ValidAnalysisJson"/> (o la respuesta por defecto indicada). Registra cada llamada. Es el
/// cliente por defecto de <see cref="CodeNetixFactory"/>: ningún test llama a un Proveedor real.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    public const string ValidSpecJson =
        """
        {
          "problem": "El botón Guardar del perfil no persiste los cambios.",
          "probableCause": "El controlador no llama a SaveChangesAsync.",
          "proposedChange": "Llamar a SaveChangesAsync en ProfileController.Save y devolver el error si falla.",
          "acceptanceCriteria": ["Al pulsar Guardar los cambios del perfil se persisten."],
          "affectedFiles": ["src/ProfileController.cs"]
        }
        """;

    /// <summary><see cref="ValidSpecJson"/> en el envoltorio de <c>schemas/analysis.v1.json</c> (respuesta por defecto).</summary>
    public static readonly string ValidAnalysisJson = SpecAnswer(ValidSpecJson);

    /// <summary>Veredicto válido por defecto (<c>schemas/verdict.v1.json</c>).</summary>
    public const string ValidVerdictJson =
        """
        {
          "decision": "approved",
          "summary": "Los cambios cumplen la Spec sin hallazgos bloqueantes.",
          "findings": []
        }
        """;

    private readonly ConcurrentQueue<Func<CancellationToken, Task<string>>> _script = new();
    private readonly ConcurrentQueue<Call> _calls = new();
    private readonly string _defaultResponse;
    private readonly string _defaultVerdictResponse;

    /// <param name="defaultResponse">Respuesta sin guion; por defecto <see cref="ValidAnalysisJson"/>.</param>
    public FakeChatClient(string? defaultResponse = null, string? defaultVerdictResponse = null)
    {
        _defaultResponse = defaultResponse ?? ValidAnalysisJson;
        _defaultVerdictResponse = defaultVerdictResponse ?? ValidVerdictJson;
    }

    /// <summary>Manejador dinámico opcional por llamada (cuando hay paralelismo o respuestas por especialidad).</summary>
    public Func<Call, CancellationToken, Task<string>>? Handler { get; set; }

    /// <summary>Uso de tokens opcional que se adjunta a cada <see cref="ChatResponse"/> devuelta.</summary>
    public UsageDetails? DefaultUsage { get; set; }

    /// <summary><c>{"outcome":"spec","spec":&lt;specJson&gt;}</c>.</summary>
    public static string SpecAnswer(string specJson) => $$"""{"outcome":"spec","spec":{{specJson}}}""";

    /// <summary><c>{"outcome":"not_automatable","category":…,"explanation":…}</c>.</summary>
    public static string NotAutomatableAnswer(string category, string explanation) =>
        System.Text.Json.JsonSerializer.Serialize(new { outcome = "not_automatable", category, explanation });

    public IReadOnlyList<Call> Calls => [.. _calls];

    /// <summary>Encola respuestas de texto.</summary>
    public FakeChatClient Respond(params string[] texts)
    {
        foreach (var text in texts)
        {
            _script.Enqueue(_ => Task.FromResult(text));
        }

        return this;
    }

    /// <summary>Encola un error del Proveedor.</summary>
    public FakeChatClient Throw(Exception error)
    {
        _script.Enqueue(_ => Task.FromException<string>(error));
        return this;
    }

    /// <summary>Encola una llamada que no responde hasta que se cancela (timeout).</summary>
    public FakeChatClient Hang()
    {
        _script.Enqueue(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return string.Empty;
        });
        return this;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = new Call([.. messages.Select(m => (m.Role, m.Text))], options);
        _calls.Enqueue(call);

        string text;
        if (Handler is not null)
        {
            text = await Handler(call, cancellationToken);
        }
        else if (_script.TryDequeue(out var next))
        {
            text = await next(cancellationToken);
        }
        else
        {
            var isVerdictCall = options?.ResponseFormat is ChatResponseFormatJson { SchemaName: VerdictSchema.Name }
                || call.Messages.Any(m => m.Role == ChatRole.System && m.Text.Contains("Reviewer of CodeNetix", StringComparison.Ordinal));
            text = isVerdictCall ? _defaultVerdictResponse : _defaultResponse;
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = DefaultUsage,
        };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("El Analista no usa streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // El cliente es compartido entre llamadas: no hay nada que liberar.
    }

    internal sealed record Call(IReadOnlyList<(ChatRole Role, string Text)> Messages, ChatOptions? Options);
}

/// <summary>Fábrica que siempre devuelve el mismo <see cref="FakeChatClient"/> y registra con qué Proveedor y modelo.</summary>
internal sealed class FakeChatClientFactory(FakeChatClient client) : IAgentChatClientFactory
{
    private readonly ConcurrentQueue<(AgentEndpoint Endpoint, string Model)> _created = new();

    public IReadOnlyList<(AgentEndpoint Endpoint, string Model)> Created => [.. _created];

    public IChatClient Create(AgentEndpoint endpoint, string model)
    {
        _created.Enqueue((endpoint, model));
        return client;
    }
}
