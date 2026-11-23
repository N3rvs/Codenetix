using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Host.Tests;

/// <summary>
/// API de Sentry falsa en Kestrel (127.0.0.1, puerto libre): <c>GET /api/0/organizations/{org}/issues/{id}/</c> y
/// <c>GET /api/0/organizations/{org}/issues/{id}/events/latest/</c>. Por defecto sirve <see cref="DefaultIssue"/> y
/// <see cref="DefaultEvent"/> para cualquier id; las propiedades se pueden cambiar en cualquier momento. Registra cada
/// petición con su cabecera <c>Authorization</c>.
/// </summary>
internal sealed class FakeSentry : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Request> _requests = new();

    private FakeSentry()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _app.MapGet("/api/0/organizations/{org}/issues/{id}/", async (HttpContext context, string org, string id) =>
        {
            Record(context, $"issue:{org}/{id}");
            await Task.Delay(Delay, context.RequestAborted);
            if (RedirectTo is { } location)
            {
                context.Response.Headers.Location = location;
                return Results.StatusCode(StatusCodes.Status302Found);
            }

            if (IssueStatus != StatusCodes.Status200OK)
            {
                return Results.Json(new { detail = "error " + AuthToken }, statusCode: IssueStatus);
            }

            return Results.Text(IssueJson(id), "application/json");
        });

        _app.MapGet("/api/0/organizations/{org}/issues/{id}/events/latest/", async (HttpContext context, string org, string id) =>
        {
            Record(context, $"latest:{org}/{id}");
            await Task.Delay(Delay, context.RequestAborted);
            if (EventStatus != StatusCodes.Status200OK)
            {
                return Results.Json(new { detail = "error" }, statusCode: EventStatus);
            }

            return Results.Text(EventJson ?? DefaultEvent(), "application/json");
        });
    }

    /// <summary>Lo que el fake devuelve en los errores, para comprobar que nunca se cita.</summary>
    public string AuthToken { get; set; } = string.Empty;

    public string BaseUrl { get; private set; } = string.Empty;

    public int IssueStatus { get; set; } = StatusCodes.Status200OK;

    public int EventStatus { get; set; } = StatusCodes.Status200OK;

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>Con valor, <c>GET …/issues/{id}/</c> responde <c>302</c> con esta <c>Location</c>.</summary>
    public string? RedirectTo { get; set; }

    public string ProjectSlug { get; set; } = "front-end";

    /// <summary>Evento completo a servir; <c>null</c> = <see cref="DefaultEvent"/>.</summary>
    public string? EventJson { get; set; }

    public IReadOnlyList<Request> Requests => [.. _requests];

    public static async Task<FakeSentry> StartAsync(CancellationToken cancellationToken = default)
    {
        var fake = new FakeSentry();
        await fake._app.StartAsync(cancellationToken);
        fake.BaseUrl = fake._app.Urls.First().TrimEnd('/');
        return fake;
    }

    public string IssueJson(string id) => JsonSerializer.Serialize(new
    {
        id,
        shortId = "FRONT-END-1",
        title = "NullReferenceException: Object reference not set to an instance of an object.",
        culprit = "App.Controllers.ProfileController.Save",
        level = "error",
        status = "unresolved",
        count = "42",
        userCount = 7,
        firstSeen = "2026-11-20T08:00:00Z",
        lastSeen = "2026-11-23T09:00:00Z",
        permalink = $"https://acme.sentry.io/issues/{id}/",
        project = new { id = "1", slug = ProjectSlug, name = ProjectSlug },
    });

    /// <summary>Evento razonable: excepción con marcos de biblioteca y de la aplicación, migas, release y etiquetas.</summary>
    public static string DefaultEvent(
        string? exceptionValue = null,
        IEnumerable<object>? frames = null,
        IEnumerable<object>? breadcrumbs = null,
        object? request = null,
        object? user = null,
        IEnumerable<object>? extraTags = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["eventID"] = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
            ["dateCreated"] = "2026-11-23T09:00:00Z",
            ["release"] = new { version = "web@1.4.2" },
            ["tags"] = new object[]
            {
                new { key = "environment", value = "production" },
                new { key = "release", value = "web@1.4.2" },
                new { key = "handled", value = "no" },
                new { key = "runtime", value = ".NET 10.0" },
                new { key = "server_name", value = "prod-web-01.internal" },
            }.Concat(extraTags ?? []).ToArray(),
            ["entries"] = new object?[]
            {
                new
                {
                    type = "exception",
                    data = new
                    {
                        values = new object[]
                        {
                            new
                            {
                                type = "System.NullReferenceException",
                                value = exceptionValue ?? "Object reference not set to an instance of an object.",
                                mechanism = new { type = "generic", handled = false },
                                stacktrace = new
                                {
                                    frames = (frames ?? DefaultFrames()).ToArray(),
                                },
                            },
                        },
                    },
                },
                new
                {
                    type = "breadcrumbs",
                    data = new
                    {
                        values = (breadcrumbs ?? [new { timestamp = "2026-11-23T08:59:59Z", category = "http", level = "info", message = "POST /profile", data = new { url = "https://app.example/profile" } }]).ToArray(),
                    },
                },
                request is null ? null : new { type = "request", data = request },
            }.Where(e => e is not null).ToArray(),
            ["user"] = user,
            ["contexts"] = new { os = new { name = "Linux" }, device = new { model = "secret-device-model" } },
            ["extra"] = new { card = "4111111111111111" },
            ["sdk"] = new { name = "sentry.dotnet", version = "5.0.0" },
        });

    /// <summary>Marcos en el orden de Sentry (del más antiguo al más reciente): biblioteca, aplicación, biblioteca.</summary>
    public static IEnumerable<object> DefaultFrames() =>
    [
        new { function = "Invoke", module = "Microsoft.AspNetCore.Mvc", filename = "ControllerActionInvoker.cs", lineNo = 100, inApp = false },
        new { function = "Save", module = "App.Controllers.ProfileController", filename = "src/Controllers/ProfileController.cs", absPath = "/home/victimuser/src/Controllers/ProfileController.cs", lineNo = 42, colNo = 13, inApp = true, context = new object[] { new object[] { 41, "    var user = await _users.FindAsync(id);" }, new object[] { 42, "    var name = user.Name.Trim();" } }, vars = new { password = "varsecret-123" } },
        new { function = "get_Name", module = "App.Models.User", filename = "src/Models/User.cs", lineNo = 7, inApp = false },
    ];

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private void Record(HttpContext context, string kind) =>
        _requests.Enqueue(new Request(kind, context.Request.Path + context.Request.QueryString, context.Request.Headers.Authorization.ToString()));

    internal sealed record Request(string Kind, string PathAndQuery, string Authorization);
}
