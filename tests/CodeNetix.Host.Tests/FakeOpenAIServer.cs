using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Servidor falso compatible con OpenAI en Kestrel (127.0.0.1, puerto libre): <c>POST /v1/chat/completions</c>
/// responde <see cref="Content"/> (por defecto una Spec válida) o el estado <see cref="Status"/>. Registra el
/// cuerpo, la URL y todas las cabeceras de cada petición.
/// </summary>
internal sealed class FakeOpenAIServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Request> _requests = new();

    private FakeOpenAIServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            _requests.Enqueue(new Request(
                context.Request.Path + context.Request.QueryString,
                body,
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));

            if (Status != StatusCodes.Status200OK)
            {
                return Results.Json(new { error = new { message = ErrorMessage, type = "server_error" } }, statusCode: Status);
            }

            var reply = body.Contains("Reviewer of CodeNetix", StringComparison.Ordinal)
                ? ReviewerContent
                : Content;

            return Results.Json(new
            {
                id = "chatcmpl-test",
                @object = "chat.completion",
                created = 1_700_000_000,
                model = "local-model",
                choices = new[]
                {
                    new { index = 0, message = new { role = "assistant", content = reply }, finish_reason = "stop" },
                },
                usage = new { prompt_tokens = 10, completion_tokens = 10, total_tokens = 20 },
            });
        });

        _app.MapFallback((HttpContext context) =>
        {
            _requests.Enqueue(new Request(context.Request.Path.ToString(), string.Empty, new Dictionary<string, string>()));
            return Results.NotFound();
        });
    }

    public string Content { get; set; } = FakeChatClient.ValidAnalysisJson;

    public string ReviewerContent { get; set; } = FakeChatClient.ValidVerdictJson;

    public int Status { get; set; } = StatusCodes.Status200OK;

    /// <summary><c>error.message</c> de las respuestas de error.</summary>
    public string ErrorMessage { get; set; } = "upstream exploded";

    /// <summary><c>http://127.0.0.1:&lt;puerto&gt;/v1/</c>.</summary>
    public string BaseUrl => _app.Urls.First() + "/v1/";

    public IReadOnlyList<Request> Requests => [.. _requests];

    public static async Task<FakeOpenAIServer> StartAsync(CancellationToken cancellationToken = default)
    {
        var server = new FakeOpenAIServer();
        await server._app.StartAsync(cancellationToken);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }

    internal sealed record Request(string PathAndQuery, string Body, IReadOnlyDictionary<string, string> Headers)
    {
        public JsonDocument Json() => JsonDocument.Parse(Body);
    }
}
