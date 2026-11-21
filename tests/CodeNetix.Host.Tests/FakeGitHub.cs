using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodeNetix.Host.Tests;

/// <summary>
/// API de GitHub falsa en Kestrel (127.0.0.1, puerto libre): <c>GET /user</c> y
/// <c>GET /repos/{o}/{r}/collaborators/{login}/permission</c> y <c>GET /repos/{o}/{r}/issues/{n}</c>. Por defecto responde <c>login = bot</c> y
/// permiso <c>write</c>; las propiedades se pueden cambiar en cualquier momento. Registra cada petición.
/// </summary>
internal sealed class FakeGitHub : IAsyncDisposable
{
    public const string DefaultLogin = "bot";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Request> _requests = new();

    private FakeGitHub()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        // Raíz (github.com) y /api/v3 (GitHub Enterprise).
        foreach (var prefix in new[] { string.Empty, EnterprisePrefix })
        {
            _app.MapGet(prefix + "/user", async (HttpContext context) =>
            {
                Record(context, "user");
                await DelayAsync(context);
                if (UserStatus == StatusCodes.Status200OK)
                {
                    return Results.Json(new { login = Login, id = 1 });
                }

                AddRateLimitHeader(context);
                return Results.Json(new { message = "Bad credentials" }, statusCode: UserStatus);
            });

            _app.MapGet(prefix + "/repos/{owner}/{repo}/collaborators/{login}/permission", async (HttpContext context, string owner, string repo, string login) =>
            {
                Record(context, $"permission:{owner}/{repo}:{login}");
                await DelayAsync(context);
                if (Redirects.TryGetValue($"{owner}/{repo}", out var location))
                {
                    context.Response.Headers.Location = location;
                    return Results.StatusCode(StatusCodes.Status301MovedPermanently);
                }

                if (PermissionStatus != StatusCodes.Status200OK)
                {
                    AddRateLimitHeader(context);
                    return Results.Json(new { message = "Not Found" }, statusCode: PermissionStatus);
                }

                return OmitPermission
                    ? Results.Json(new { role_name = Permission, user = new { login } })
                    : Results.Json(new { permission = Permission, role_name = Permission, user = new { login } });
            });
            _app.MapGet(prefix + "/repos/{owner}/{repo}/issues/{number}", async (HttpContext context, string owner, string repo, string number) =>
            {
                Record(context, $"issue:{owner}/{repo}#{number}");
                await DelayAsync(context);
                if (IssueStatus != StatusCodes.Status200OK)
                {
                    AddRateLimitHeader(context);
                    return Results.Json(new { message = "Not Found" }, statusCode: IssueStatus);
                }

                return Results.Json(new { number = long.Parse(number, System.Globalization.CultureInfo.InvariantCulture), title = IssueTitle, body = IssueBody, state = "open", comments = CommentsOf(owner, repo, number).Count });
            });

            _app.MapGet(prefix + "/repos/{owner}/{repo}/issues/{number}/comments", (HttpContext context, string owner, string repo, string number) =>
            {
                Record(context, $"comments:{owner}/{repo}#{number}:page={context.Request.Query["page"]}");
                if (CommentListStatus != StatusCodes.Status200OK)
                {
                    return Results.Json(new { message = "Server Error" }, statusCode: CommentListStatus);
                }

                var size = Math.Clamp(int.TryParse(context.Request.Query["per_page"], out var perPage) ? perPage : 30, 1, 100);
                var index = Math.Max(1, int.TryParse(context.Request.Query["page"], out var page) ? page : 1);
                var list = CommentsOf(owner, repo, number);
                FakeComment[] slice;
                int total;
                lock (list)
                {
                    total = list.Count;
                    slice = [.. list.Skip((index - 1) * size).Take(size)];
                }

                // Como GitHub: Link solo si hay más de una página (next/last, prev/first según la posición).
                var last = Math.Max(1, (total + size - 1) / size);
                if (last > 1)
                {
                    var url = $"{BaseUrl}{context.Request.Path}?per_page={size}&page=";
                    var parts = new List<string>();
                    if (index < last)
                    {
                        parts.Add($"<{url}{index + 1}>; rel=\"next\"");
                        parts.Add($"<{url}{last}>; rel=\"last\"");
                    }

                    if (index > 1)
                    {
                        parts.Add($"<{url}{index - 1}>; rel=\"prev\"");
                        parts.Add($"<{url}1>; rel=\"first\"");
                    }

                    context.Response.Headers["Link"] = string.Join(", ", parts);
                }


                return Results.Json(slice.Select(c => new { id = c.Id, html_url = c.HtmlUrl, body = c.Body, user = new { login = c.Login } }));
            });

            _app.MapPost(prefix + "/repos/{owner}/{repo}/issues/{number}/comments", async (HttpContext context, string owner, string repo, string number) =>
            {
                Record(context, $"comment-post:{owner}/{repo}#{number}");
                using var payload = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                if (CommentPostDelay > TimeSpan.Zero)
                {
                    await Task.Delay(CommentPostDelay, context.RequestAborted);
                }

                if (CommentPostStatus != StatusCodes.Status201Created)
                {
                    AddRateLimitHeader(context);
                    return Results.Json(new { message = "Resource not accessible by integration" }, statusCode: CommentPostStatus);
                }

                var comment = AddComment(owner, repo, number, payload.RootElement.GetProperty("body").GetString()!, Login);
                return Results.Json(new { id = comment.Id, html_url = comment.HtmlUrl, body = comment.Body, user = new { login = comment.Login } }, statusCode: StatusCodes.Status201Created);
            });

            _app.MapPost(prefix + "/repos/{owner}/{repo}/pulls", async (HttpContext context, string owner, string repo) =>
            {
                Record(context, $"pr-post:{owner}/{repo}");
                using var payload = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                await DelayAsync(context);
                if (PullRequestPostStatus != StatusCodes.Status201Created)
                {
                    AddRateLimitHeader(context);
                    return Results.Json(new { message = "Validation Failed" }, statusCode: PullRequestPostStatus);
                }

                var root = payload.RootElement;
                var title = root.GetProperty("title").GetString()!;
                var head = root.GetProperty("head").GetString()!;
                var baseBranch = root.GetProperty("base").GetString()!;
                var body = root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == System.Text.Json.JsonValueKind.String
                    ? bodyEl.GetString()!
                    : string.Empty;
                var pr = AddPullRequest(owner, repo, title, head, baseBranch, body);
                return Results.Json(
                    new
                    {
                        number = pr.Number,
                        html_url = pr.HtmlUrl,
                        title = pr.Title,
                        body = pr.Body,
                        head = new { @ref = pr.Head },
                        @base = new { @ref = pr.Base },
                    },
                    statusCode: StatusCodes.Status201Created);
            });

            _app.MapPost(prefix + "/repos/{owner}/{repo}/issues/{number}/labels", async (HttpContext context, string owner, string repo, string number) =>
            {
                Record(context, $"label-post:{owner}/{repo}#{number}");
                using var payload = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                await DelayAsync(context);
                if (LabelPostStatus is < 200 or >= 300)
                {
                    AddRateLimitHeader(context);
                    return Results.Json(new { message = "Forbidden" }, statusCode: LabelPostStatus);
                }

                var labels = payload.RootElement.GetProperty("labels")
                    .EnumerateArray()
                    .Select(e => e.GetString()!)
                    .ToArray();
                AddLabels(owner, repo, number, labels);
                return Results.Json(labels.Select(l => new { name = l }), statusCode: LabelPostStatus);
            });
        }

        // Cualquier otra ruta (p. ej. el protocolo HTTP de git en un clon): se registra y responde 404.
        _app.MapFallback((HttpContext context) =>
        {
            Record(context, "other");
            return Results.NotFound();
        });
    }

    public const string EnterprisePrefix = "/api/v3";

    public const string DefaultIssueTitle = "El botón Guardar no hace nada";

    public const string DefaultIssueBody = "Al pulsar Guardar en la página de perfil no se guarda nada y no aparece ningún error.";

    /// <summary>Respuesta de <c>GET /repos/{o}/{r}/issues/{n}</c> (por defecto 200).</summary>
    public int IssueStatus { get; set; } = StatusCodes.Status200OK;

    public string IssueTitle { get; set; } = DefaultIssueTitle;

    /// <summary><c>null</c> = issue sin descripción.</summary>
    public string? IssueBody { get; set; } = DefaultIssueBody;

    public int IssueCalls => _requests.Count(r => r.Kind.StartsWith("issue:", StringComparison.Ordinal));

    private readonly ConcurrentDictionary<string, List<FakeComment>> _comments = new(StringComparer.Ordinal);
    private long _nextCommentId = 1000;

    /// <summary>Respuesta de <c>POST …/issues/{n}/comments</c> (por defecto 201).</summary>
    public int CommentPostStatus { get; set; } = StatusCodes.Status201Created;

    /// <summary>Respuesta de <c>GET …/issues/{n}/comments</c> (por defecto 200).</summary>
    public int CommentListStatus { get; set; } = StatusCodes.Status200OK;

    /// <summary>Retraso antes de crear un comentario (para provocar llamadas concurrentes).</summary>
    public TimeSpan CommentPostDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Peticiones <c>POST</c> de comentarios recibidas (con éxito o no).</summary>
    public int CommentPosts => _requests.Count(r => r.Kind.StartsWith("comment-post:", StringComparison.Ordinal));

    /// <summary>Páginas (<c>page=</c>) pedidas al listar los comentarios de <c>owner/repo#number</c>, en orden.</summary>
    public IReadOnlyList<string> CommentPagesListed(string owner, string repo, long number)
    {
        var prefix = $"comments:{owner}/{repo}#{number}:page=";
        return [.. _requests.Where(r => r.Kind.StartsWith(prefix, StringComparison.Ordinal)).Select(r => r.Kind[prefix.Length..])];
    }

    /// <summary>Comentarios de la issue <c>owner/repo#number</c>, en orden de creación.</summary>
    public IReadOnlyList<FakeComment> CommentsOn(string owner, string repo, long number)
    {
        var list = CommentsOf(owner, repo, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        lock (list)
        {
            return [.. list];
        }
    }

    /// <summary>Añade un comentario (también lo usan los tests para sembrar comentarios previos).</summary>
    public FakeComment AddComment(string owner, string repo, string number, string body, string login)
    {
        var id = Interlocked.Increment(ref _nextCommentId);
        var comment = new FakeComment(id, $"https://github.com/{owner}/{repo}/issues/{number}#issuecomment-{id}", body, login);
        var list = CommentsOf(owner, repo, number);
        lock (list)
        {
            list.Add(comment);
        }

        return comment;
    }

    private List<FakeComment> CommentsOf(string owner, string repo, string number) =>
        _comments.GetOrAdd($"{owner}/{repo}#{number}", _ => []);

    internal sealed record FakeComment(long Id, string HtmlUrl, string Body, string Login);

    private readonly ConcurrentDictionary<string, List<FakePullRequest>> _pullRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<string>> _labels = new(StringComparer.Ordinal);
    private long _nextPullRequestNumber = 100;

    /// <summary>Respuesta de <c>POST /repos/{o}/{r}/pulls</c> (por defecto 201).</summary>
    public int PullRequestPostStatus { get; set; } = StatusCodes.Status201Created;

    /// <summary>Respuesta de <c>POST /repos/{o}/{r}/issues/{n}/labels</c> (por defecto 200).</summary>
    public int LabelPostStatus { get; set; } = StatusCodes.Status200OK;

    public int PullRequestPosts => _requests.Count(r => r.Kind.StartsWith("pr-post:", StringComparison.Ordinal));

    public int LabelPosts => _requests.Count(r => r.Kind.StartsWith("label-post:", StringComparison.Ordinal));

    public IReadOnlyList<FakePullRequest> PullRequestsOn(string owner, string repo)
    {
        var list = _pullRequests.GetOrAdd($"{owner}/{repo}", _ => []);
        lock (list)
        {
            return [.. list];
        }
    }

    public IReadOnlyList<string> LabelsOn(string owner, string repo, long number)
    {
        var list = _labels.GetOrAdd($"{owner}/{repo}#{number}", _ => []);
        lock (list)
        {
            return [.. list];
        }
    }

    private FakePullRequest AddPullRequest(string owner, string repo, string title, string head, string baseBranch, string body)
    {
        var number = Interlocked.Increment(ref _nextPullRequestNumber);
        var pr = new FakePullRequest(number, $"https://github.com/{owner}/{repo}/pull/{number}", title, head, baseBranch, body);
        var list = _pullRequests.GetOrAdd($"{owner}/{repo}", _ => []);
        lock (list)
        {
            list.Add(pr);
        }

        return pr;
    }

    private void AddLabels(string owner, string repo, string number, IReadOnlyList<string> labels)
    {
        var list = _labels.GetOrAdd($"{owner}/{repo}#{number}", _ => []);
        lock (list)
        {
            list.AddRange(labels);
        }
    }

    internal sealed record FakePullRequest(long Number, string HtmlUrl, string Title, string Head, string Base, string Body);

    public string Login { get; set; } = DefaultLogin;

    public int UserStatus { get; set; } = StatusCodes.Status200OK;

    public string Permission { get; set; } = "write";

    public int PermissionStatus { get; set; } = StatusCodes.Status200OK;

    /// <summary>200 de permiso sin el campo <c>permission</c>.</summary>
    public bool OmitPermission { get; set; }

    /// <summary>En respuestas de error, añade <c>x-ratelimit-remaining: 0</c>.</summary>
    public bool RateLimitExhausted { get; set; }

    /// <summary><c>owner/repo</c> → <c>Location</c> de un 301 en la consulta de permiso (repositorio renombrado).</summary>
    public ConcurrentDictionary<string, string> Redirects { get; } = new(StringComparer.Ordinal);

    private void AddRateLimitHeader(HttpContext context)
    {
        if (RateLimitExhausted)
        {
            context.Response.Headers["x-ratelimit-remaining"] = "0";
        }
    }

    /// <summary>Retraso antes de responder (para probar timeouts).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public string BaseUrl => _app.Urls.First();

    public IReadOnlyList<Request> Requests => [.. _requests];

    public int UserCalls => _requests.Count(r => r.Kind == "user");

    public int PermissionCalls => _requests.Count(r => r.Kind.StartsWith("permission:", StringComparison.Ordinal));

    public int TotalCalls => _requests.Count;

    public static async Task<FakeGitHub> StartAsync(CancellationToken cancellationToken = default)
    {
        var fake = new FakeGitHub();
        await fake._app.StartAsync(cancellationToken);
        return fake;
    }

    private void Record(HttpContext context, string kind) => _requests.Enqueue(new Request(
        kind,
        context.Request.Path.ToString(),
        context.Request.Headers.Authorization.ToString(),
        context.Request.Headers.Accept.ToString(),
        context.Request.Headers["X-GitHub-Api-Version"].ToString(),
        context.Request.Headers.UserAgent.ToString()));

    private async Task DelayAsync(HttpContext context)
    {
        if (Delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(Delay, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // El cliente cortó la petición (timeout).
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }

    internal sealed record Request(string Kind, string Path, string Authorization, string Accept, string ApiVersion, string UserAgent);
}
