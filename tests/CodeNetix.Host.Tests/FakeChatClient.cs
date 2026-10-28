using System.Collections.Concurrent;
using CodeNetix.Agents;
using CodeNetix.Agents.ChatClients;
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

    private readonly ConcurrentQueue<Func<CancellationToken, Task<string>>> _script = new();
    private readonly ConcurrentQueue<Call> _calls = new();
    private readonly string _defaultResponse;

    /// <param name="defaultResponse">Respuesta sin guion; por defecto <see cref="ValidAnalysisJson"/>.</param>
    public FakeChatClient(string? defaultResponse = null) => _defaultResponse = defaultResponse ?? ValidAnalysisJson;

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
        _calls.Enqueue(new Call([.. messages.Select(m => (m.Role, m.Text))], options));
        var step = _script.TryDequeue(out var next) ? next : _ => Task.FromResult(_defaultResponse);
        var text = await step(cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
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
