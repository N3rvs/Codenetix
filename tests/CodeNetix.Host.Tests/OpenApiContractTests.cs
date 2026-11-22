using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeNetix.Contracts;
using CodeNetix.Host.Features.CancelRun;
using CodeNetix.Host.Features.GetEvent;
using CodeNetix.Host.Features.GetRun;
using CodeNetix.Host.Features.SubmitManualEvent;
using Microsoft.Data.Sqlite;

namespace CodeNetix.Host.Tests;

/// <summary>
/// Prueba de contrato OpenAPI (AD-14, Historia 4.1): descarga <c>/openapi/v1.json</c> del Host real,
/// comprueba que no existe desfase con <c>panel/openapi/v1.json</c> y valida ejemplos serializados de
/// <see cref="RunSummary"/> y de cada subtipo concreto de <see cref="RunEvent"/> contra el esquema publicado.
/// </summary>
public sealed class OpenApiContractTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codenetix-openapi-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _webRoot;

    public OpenApiContractTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><html><body>ok</body></html>");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenApi_document_requires_bearer_token_and_rejects_query_access_token()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var anonymous = factory.CreateClient();

        using var noToken = await anonymous.GetAsync("/openapi/v1.json", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        using var queryToken = await anonymous.GetAsync(
            $"/openapi/v1.json?access_token={Uri.EscapeDataString(CodeNetixFactory.TestAccessToken)}",
            Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, queryToken.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_matches_panel_snapshot_and_validates_RunSummary_and_every_RunEvent_subtype()
    {
        await using var factory = new CodeNetixFactory(_dataDirectory, _webRoot, CodeNetixFactory.CompleteSettings);
        using var client = factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/openapi/v1.json", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rawJson = await response.Content.ReadAsStringAsync(Ct);
        var doc = JsonNode.Parse(rawJson)?.AsObject()
            ?? throw new InvalidOperationException("El documento OpenAPI no es un objeto JSON válido.");

        // Comprobar drift estructural respecto a panel/openapi/v1.json (ignorando 'servers').
        var snapshotPath = FindRepoFile(Path.Combine("panel", "openapi", "v1.json"));
        var snapshotJson = await File.ReadAllTextAsync(snapshotPath, Ct);
        var snapshotDoc = JsonNode.Parse(snapshotJson)?.AsObject()
            ?? throw new InvalidOperationException($"No se pudo parsear {snapshotPath}.");

        var liveNormalized = doc.DeepClone().AsObject();
        var snapshotNormalized = snapshotDoc.DeepClone().AsObject();
        liveNormalized.Remove("servers");
        snapshotNormalized.Remove("servers");

        Assert.True(
            JsonNode.DeepEquals(liveNormalized, snapshotNormalized),
            $"Desfase detectado entre /openapi/v1.json del Host y {snapshotPath}. Exporta el nuevo /openapi/v1.json a panel/openapi/v1.json y ejecuta 'npm --prefix panel run generate:api'.");

        var paths = doc["paths"]?.AsObject()
            ?? throw new InvalidOperationException("Falta 'paths' en /openapi/v1.json.");

        Assert.True(paths.ContainsKey("/api/runs"), "Falta '/api/runs' en OpenAPI.");
        Assert.True(paths.ContainsKey("/api/runs/{id}"), "Falta '/api/runs/{id}' en OpenAPI.");
        Assert.True(paths.ContainsKey("/api/runs/{id}/events"), "Falta '/api/runs/{id}/events' en OpenAPI.");
        Assert.True(paths.ContainsKey("/api/runs/{id}/cancel"), "Falta '/api/runs/{id}/cancel' en OpenAPI.");
        Assert.True(paths.ContainsKey("/api/events/manual"), "Falta '/api/events/manual' en OpenAPI.");
        Assert.True(paths.ContainsKey("/api/events/{eventId}"), "Falta '/api/events/{eventId}' en OpenAPI.");

        var schemas = doc["components"]?["schemas"]?.AsObject()
            ?? throw new InvalidOperationException("Falta 'components.schemas' en /openapi/v1.json.");

        // Validar un RunSummary serializado contra el esquema 200 de GET /api/runs.
        var runsArraySchema = paths["/api/runs"]?["get"]?["responses"]?["200"]?["content"]?["application/json"]?["schema"]?.AsObject()
            ?? throw new InvalidOperationException("Falta el esquema de respuesta 200 de GET /api/runs.");
        var runSummarySchema = ResolveSchema(runsArraySchema["items"]?.AsObject()
            ?? throw new InvalidOperationException("La respuesta de GET /api/runs no es un array con 'items'."), schemas);

        var sampleSummary = new RunSummary(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "11111111",
            RunState.Failed,
            FailureReason.TestsFailed,
            "2 tests fallaron",
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "github:owner/repo#42",
            new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 21, 12, 5, 0, TimeSpan.Zero));
        var serializedSummary = JsonSerializer.SerializeToNode(sampleSummary, WebJson)?.AsObject()
            ?? throw new InvalidOperationException("No se pudo serializar RunSummary.");
        ValidateObjectAgainstSchema(serializedSummary, runSummarySchema, schemas, nameof(RunSummary));

        // Validar un RunResponse completo (Spec, TestReport, Verdicts, PullRequest y Events) contra GET /api/runs/{id} -> 200.
        var runResponseSchema = ResolveSchema(
            paths["/api/runs/{id}"]?["get"]?["responses"]?["200"]?["content"]?["application/json"]?["schema"]?.AsObject()
                ?? throw new InvalidOperationException("Falta el esquema de respuesta 200 de GET /api/runs/{id}."),
            schemas);

        var detailRunId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var detailStageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var detailEventId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var detailAt = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

        var sampleRunResponse = new RunResponse(
            detailRunId,
            "11111111",
            RunState.PullRequestCreated,
            null,
            null,
            detailEventId,
            "github:owner/repo#42",
            detailAt,
            detailAt.AddMinutes(3),
            new Spec(
                "Error al validar límite.",
                "Falta comprobación de tope máximo.",
                "Aplicar Math.Min en ListRunsEndpoint.",
                ["GET /api/runs?limit=500 responde 200"],
                ["src/CodeNetix.Host/Features/ListRuns/ListRunsEndpoint.cs"]),
            new TestReport(
                BuildSucceeded: true,
                TotalTests: 11,
                PassedTests: 10,
                FailedTests: 1,
                SkippedTests: 0,
                NewTests: ["CodeNetix.Host.Tests.SampleTest"],
                FailedTestCases: [new TestCaseResult("CodeNetix.Host.Tests.FailingSample", "Failed", "Assert.Equal() Failure", "at Sample.cs:12")],
                Duration: TimeSpan.FromSeconds(2.5)),
            [
                new RunVerdict(
                    detailStageId,
                    1,
                    ReviewerKind.Security,
                    VerdictDecision.Approved,
                    "Cambio seguro y acotado.",
                    [
                        new ReviewFinding("info", "src/CodeNetix.Host/Features/ListRuns/ListRunsEndpoint.cs", 39, "Correcto."),
                        new ReviewFinding("low", "src/CodeNetix.Host/Features/ListRuns/ListRunsEndpoint.cs", null, "Observación general de archivo."),
                    ],
                    "claude-sonnet-4-5",
                    1,
                    detailAt.AddMinutes(2)),
            ],
            new PullRequestInfo(
                7,
                "https://github.com/owner/repo/pull/7",
                "codenetix/11111111-1111-1111-1111-111111111111",
                "abcdef0123456789abcdef0123456789abcdef01",
                "Fix: validar limit en /api/runs",
                detailAt.AddMinutes(3)),
            [
                new TokensConsumed("analyst", "anthropic", "claude-sonnet-4-5", 1000, 300) with { RunId = detailRunId, Seq = 1, StageId = detailStageId, At = detailAt },
                new RiskClassified(RiskClass.Minor, 12, []) with { RunId = detailRunId, Seq = 2, StageId = detailStageId, At = detailAt.AddMinutes(2) },
                new PullRequestOpened(7, "https://github.com/owner/repo/pull/7", "codenetix/11111111-1111-1111-1111-111111111111", "abcdef0123456789") with { RunId = detailRunId, Seq = 3, StageId = detailStageId, At = detailAt.AddMinutes(3) },
            ]);
        var serializedRunResponse = JsonSerializer.SerializeToNode(sampleRunResponse, WebJson)?.AsObject()
            ?? throw new InvalidOperationException("No se pudo serializar RunResponse.");
        ValidateObjectAgainstSchema(serializedRunResponse, runResponseSchema, schemas, nameof(RunResponse));

        // Validar también un RunResponse fallido con campos opcionales en null y listas vacías.
        var failedRunResponse = new RunResponse(
            detailRunId,
            "11111111",
            RunState.Failed,
            FailureReason.CloneFailed,
            "Error al clonar el repositorio.",
            detailEventId,
            "github:owner/repo#43",
            detailAt,
            detailAt.AddSeconds(15),
            null,
            null,
            [],
            null,
            []);
        var serializedFailedRunResponse = JsonSerializer.SerializeToNode(failedRunResponse, WebJson)?.AsObject()
            ?? throw new InvalidOperationException("No se pudo serializar RunResponse fallido.");
        ValidateObjectAgainstSchema(serializedFailedRunResponse, runResponseSchema, schemas, $"{nameof(RunResponse)}(Failed)");

        // Validar CancelRunResponse contra POST /api/runs/{id}/cancel -> 202.
        var cancelRunResponseSchema = ResolveSchema(
            paths["/api/runs/{id}/cancel"]?["post"]?["responses"]?["202"]?["content"]?["application/json"]?["schema"]?.AsObject()
                ?? throw new InvalidOperationException("Falta el esquema de respuesta 202 de POST /api/runs/{id}/cancel."),
            schemas);
        var sampleCancelResponse = new CancelRunResponse(
            detailRunId,
            "11111111",
            RunState.Coding,
            CancellationRequested: true);
        var serializedCancelResponse = JsonSerializer.SerializeToNode(sampleCancelResponse, WebJson)?.AsObject()
            ?? throw new InvalidOperationException("No se pudo serializar CancelRunResponse.");
        ValidateObjectAgainstSchema(serializedCancelResponse, cancelRunResponseSchema, schemas, nameof(CancelRunResponse));

        // Validar SubmitManualEventRequest contra POST /api/events/manual -> requestBody.
        var submitRequestSchema = ResolveSchema(
            paths["/api/events/manual"]?["post"]?["requestBody"]?["content"]?["application/json"]?["schema"]?.AsObject()
                ?? throw new InvalidOperationException("Falta el esquema de requestBody de POST /api/events/manual."),
            schemas);
        var sampleSubmitRequest = new SubmitManualEventRequest(
            "https://github.com/owner/repo/issues/42",
            "44444444-4444-4444-8444-444444444444");
        var serializedSubmitRequest = JsonSerializer.SerializeToNode(sampleSubmitRequest, WebJson)?.AsObject()
            ?? throw new InvalidOperationException("No se pudo serializar SubmitManualEventRequest.");
        ValidateObjectAgainstSchema(serializedSubmitRequest, submitRequestSchema, schemas, nameof(SubmitManualEventRequest));

        // Validar SubmitManualEventResponse contra POST /api/events/manual -> 201 y 200.
        var submitCreatedSchema = ResolveSchema(
            paths["/api/events/manual"]?["post"]?["responses"]?["201"]?["content"]?["application/json"]?["schema"]?.AsObject()
                ?? throw new InvalidOperationException("Falta el esquema de respuesta 201 de POST /api/events/manual."),
            schemas);
        var submitExistingSchema = ResolveSchema(
            paths["/api/events/manual"]?["post"]?["responses"]?["200"]?["content"]?["application/json"]?["schema"]?.AsObject()
                ?? throw new InvalidOperationException("Falta el esquema de respuesta 200 de POST /api/events/manual."),
            schemas);

        var acceptedSubmitSample = new SubmitManualEventResponse(
            detailEventId,
            Created: true,
            Status: "Accepted",
            Reason: null,
            Actor: "dev-user",
            Repository: "owner/repo");
        var serializedAcceptedSubmit = JsonSerializer.SerializeToNode(acceptedSubmitSample, WebJson)?.AsObject()
            ?? throw new InvalidOperationException("No se pudo serializar SubmitManualEventResponse (Accepted).");
        ValidateObjectAgainstSchema(serializedAcceptedSubmit, submitCreatedSchema, schemas, $"{nameof(SubmitManualEventResponse)}(201)");

        var ignoredSubmitSample = new SubmitManualEventResponse(
            detailEventId,
            Created: false,
            Status: "Ignored",
            Reason: "no autorizado",
            Actor: "external-user",
            Repository: "owner/repo");
        var serializedIgnoredSubmit = JsonSerializer.SerializeToNode(ignoredSubmitSample, WebJson)?.AsObject()
            ?? throw new InvalidOperationException("No se pudo serializar SubmitManualEventResponse (Ignored).");
        ValidateObjectAgainstSchema(serializedIgnoredSubmit, submitExistingSchema, schemas, $"{nameof(SubmitManualEventResponse)}(200)");

        // Validar EventResponse contra GET /api/events/{eventId} -> 200.
        var eventResponseSchema = ResolveSchema(
            paths["/api/events/{eventId}"]?["get"]?["responses"]?["200"]?["content"]?["application/json"]?["schema"]?.AsObject()
                ?? throw new InvalidOperationException("Falta el esquema de respuesta 200 de GET /api/events/{eventId}."),
            schemas);

        var eventWithRunSample = new EventResponse(detailEventId, "Accepted", null, detailRunId);
        var serializedEventWithRun = JsonSerializer.SerializeToNode(eventWithRunSample, WebJson)?.AsObject()
            ?? throw new InvalidOperationException("No se pudo serializar EventResponse (con runId).");
        ValidateObjectAgainstSchema(serializedEventWithRun, eventResponseSchema, schemas, $"{nameof(EventResponse)}(WithRun)");

        var ignoredEventSample = new EventResponse(detailEventId, "Ignored", "objeto ocupado", null);
        var serializedIgnoredEvent = JsonSerializer.SerializeToNode(ignoredEventSample, WebJson)?.AsObject()
            ?? throw new InvalidOperationException("No se pudo serializar EventResponse (Ignored).");
        ValidateObjectAgainstSchema(serializedIgnoredEvent, eventResponseSchema, schemas, $"{nameof(EventResponse)}(Ignored)");

        // Localizar el esquema del elemento de GET /api/runs/{id}/events -> 200 -> application/json.
        var eventsArraySchema = paths["/api/runs/{id}/events"]?["get"]?["responses"]?["200"]?["content"]?["application/json"]?["schema"]?.AsObject()
            ?? throw new InvalidOperationException("Falta el esquema de respuesta 200 de GET /api/runs/{id}/events.");
        var runEventSchema = ResolveSchema(eventsArraySchema["items"]?.AsObject()
            ?? throw new InvalidOperationException("La respuesta de GET /api/runs/{id}/events no es un array con 'items'."), schemas);

        // Descubrir por reflexión todos los subtipos concretos de RunEvent y todos los [JsonDerivedType].
        var concreteSubtypes = typeof(RunEvent).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(RunEvent)))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var derivedAttributes = typeof(RunEvent)
            .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .ToList();

        Assert.NotEmpty(concreteSubtypes);
        Assert.Equal(concreteSubtypes.Count, derivedAttributes.Count);
        foreach (var subtype in concreteSubtypes)
        {
            Assert.Contains(derivedAttributes, a => a.DerivedType == subtype && string.Equals(a.TypeDiscriminator as string, subtype.Name, StringComparison.Ordinal));
        }

        // Extraer variantes polimórficas (anyOf / oneOf) del esquema RunEvent en OpenAPI.
        var variants = (runEventSchema["anyOf"] ?? runEventSchema["oneOf"])?.AsArray()
            ?? throw new InvalidOperationException("El esquema de RunEvent en OpenAPI debe definir 'anyOf' o 'oneOf' con sus subtipos polimórficos.");

        var variantsByDiscriminator = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var variantNode in variants)
        {
            var resolved = ResolveSchema(variantNode!.AsObject(), schemas);
            var flattened = FlattenAllOf(resolved, schemas);
            var typeProp = flattened["properties"]?["type"]?.AsObject();
            var discriminatorValue = ExtractDiscriminatorConstant(typeProp);
            if (!string.IsNullOrEmpty(discriminatorValue))
            {
                variantsByDiscriminator[discriminatorValue] = flattened;
            }
        }

        var samples = BuildRealisticSamples();

        // Validar que cada subtipo concreto tiene al menos una muestra, variante OpenAPI y cumple el esquema.
        foreach (var subtype in concreteSubtypes)
        {
            Assert.True(
                samples.TryGetValue(subtype, out var subtypeSamples) && subtypeSamples.Count > 0,
                $"Falta un ejemplo de prueba en OpenApiContractTests para el subtipo {subtype.Name}.");

            Assert.True(
                variantsByDiscriminator.TryGetValue(subtype.Name, out var variantSchema),
                $"El subtipo {subtype.Name} de RunEvent no está representado en el esquema polimórfico de /openapi/v1.json.");

            foreach (var sample in subtypeSamples!)
            {
                var serialized = JsonSerializer.SerializeToNode<RunEvent>(sample, WebJson)?.AsObject()
                    ?? throw new InvalidOperationException($"No se pudo serializar {subtype.Name}.");

                Assert.Equal(subtype.Name, serialized["type"]?.GetValue<string>());
                ValidateObjectAgainstSchema(serialized, variantSchema, schemas, subtype.Name);
            }
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"No se encontró '{relativePath}' subiendo desde {AppContext.BaseDirectory}.");
    }

    private static Dictionary<Type, List<RunEvent>> BuildRealisticSamples()
    {
        var runId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var stageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var eventId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var at = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

        RunEvent Stamp(RunEvent ev, long seq, Guid? effectiveStageId = null) =>
            ev with { RunId = runId, Seq = seq, StageId = effectiveStageId, At = at };

        var list = new RunEvent[]
        {
            // Con StageId = null y con StageId no nulo para validar ambas formas.
            Stamp(new RunCreated(eventId, "github:owner/repo#42"), 1, null),
            Stamp(new StateChanged(null, RunState.Received), 2, null),
            Stamp(new StateChanged(RunState.Received, RunState.Analyzing, null, null), 3, stageId),
            Stamp(new StateChanged(RunState.Coding, RunState.Failed, FailureReason.TestsFailed, "2 tests fallaron"), 4, stageId),
            Stamp(new RepositoryPrepared("0123456789abcdef0123456789abcdef01234567"), 5, null),
            Stamp(new RepoConfigResolved(ConfigSource.File), 6, null),
            Stamp(new AnalystOutputRejected(1, "Falta el campo summary"), 7, stageId),
            Stamp(new SpecGenerated(1, "claude-sonnet-4-5"), 8, stageId),
            Stamp(new AnalysisRejected(NotAutomatableCategory.MissingInformation, 1, "claude-sonnet-4-5"), 9, stageId),
            Stamp(new IssueCommented(NotificationKind.PullRequestCreated, "https://github.com/owner/repo/issues/42#issuecomment-1"), 10, null),
            Stamp(new NotificationFailed(NotificationKind.RunFailed, "HTTP 502"), 11, null),
            Stamp(new SandboxCreated("codenetix-sandbox:latest"), 12, null),
            Stamp(new SandboxReleased(), 13, null),
            Stamp(new TokensConsumed("analyst", "anthropic", "claude-sonnet-4-5", 1200, 450), 14, stageId),
            Stamp(new EgressBlocked("example.com:443", "host no permitido"), 15, stageId),
            Stamp(new OutputAppended("dotnet test passed\n", "stdout"), 16, stageId),
            Stamp(new OutputTruncated(OutputTruncated.DefaultMaxBytes), 17, stageId),
            Stamp(new CodingAttemptFailed(1, "TestsFailed"), 18, stageId),
            Stamp(new ReviewerOutputRejected(1, ReviewerKind.Security, 1, "JSON inválido"), 19, stageId),
            Stamp(new VerdictRecorded(1, ReviewerKind.Security, VerdictDecision.Approved, 0, "claude-sonnet-4-5"), 20, stageId),
            Stamp(new RiskClassified(RiskClass.Minor, 42, ["src/App.cs"]), 21, stageId),
            Stamp(new PullRequestOpened(7, "https://github.com/owner/repo/pull/7", "codenetix/fix-42", "abcdef0123456789"), 22, stageId),
        };

        return list.GroupBy(e => e.GetType()).ToDictionary(g => g.Key, g => g.ToList());
    }

    private static JsonObject ResolveSchema(JsonObject schema, JsonObject allSchemas)
    {
        var current = schema;
        while (current["$ref"]?.GetValue<string>() is { } refPath)
        {
            const string prefix = "#/components/schemas/";
            Assert.StartsWith(prefix, refPath, StringComparison.Ordinal);
            var name = refPath[prefix.Length..];
            current = allSchemas[name]?.AsObject()
                ?? throw new InvalidOperationException($"No se encontró el esquema referenciado '{refPath}'.");
        }

        return current;
    }

    private static JsonObject FlattenAllOf(JsonObject schema, JsonObject allSchemas)
    {
        var resolved = ResolveSchema(schema, allSchemas);
        if (resolved["allOf"] is not JsonArray allOf)
        {
            return resolved;
        }

        var mergedProps = new JsonObject();
        var mergedRequired = new HashSet<string>(StringComparer.Ordinal);

        void MergeFrom(JsonObject part)
        {
            var flat = FlattenAllOf(part, allSchemas);
            if (flat["properties"] is JsonObject props)
            {
                foreach (var (k, v) in props)
                {
                    mergedProps[k] = v?.DeepClone();
                }
            }

            if (flat["required"] is JsonArray req)
            {
                foreach (var item in req)
                {
                    if (item?.GetValue<string>() is { } r)
                    {
                        mergedRequired.Add(r);
                    }
                }
            }
        }

        foreach (var item in allOf)
        {
            MergeFrom(item!.AsObject());
        }

        if (resolved["properties"] is JsonObject ownProps)
        {
            foreach (var (k, v) in ownProps)
            {
                mergedProps[k] = v?.DeepClone();
            }
        }

        if (resolved["required"] is JsonArray ownReq)
        {
            foreach (var item in ownReq)
            {
                if (item?.GetValue<string>() is { } r)
                {
                    mergedRequired.Add(r);
                }
            }
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = mergedProps,
        };
        if (mergedRequired.Count > 0)
        {
            result["required"] = new JsonArray([.. mergedRequired.Select(r => JsonValue.Create(r))]);
        }

        return result;
    }

    private static string? ExtractDiscriminatorConstant(JsonObject? typeProp)
    {
        if (typeProp is null)
        {
            return null;
        }

        if (typeProp["const"]?.GetValue<string>() is { } c)
        {
            return c;
        }

        if (typeProp["enum"] is JsonArray { Count: 1 } arr)
        {
            return arr[0]?.GetValue<string>();
        }

        return null;
    }

    private static void ValidateObjectAgainstSchema(JsonObject instance, JsonObject schema, JsonObject allSchemas, string context)
    {
        var flat = FlattenAllOf(schema, allSchemas);
        var props = flat["properties"]?.AsObject()
            ?? throw new InvalidOperationException($"El esquema de {context} no define 'properties'.");

        if (flat["required"] is JsonArray required)
        {
            foreach (var reqNode in required)
            {
                var reqName = reqNode!.GetValue<string>();
                Assert.True(instance.ContainsKey(reqName), $"{context}: falta la propiedad requerida '{reqName}' en la instancia serializada.");
            }
        }

        foreach (var (propName, propValue) in instance)
        {
            Assert.True(
                props.TryGetPropertyValue(propName, out var propSchemaNode) && propSchemaNode is JsonObject,
                $"{context}: la propiedad serializada '{propName}' no está declarada en el esquema OpenAPI.");

            ValidateValueAgainstSchema(propValue, propSchemaNode!.AsObject(), allSchemas, $"{context}.{propName}");
        }
    }

    private static void ValidateValueAgainstSchema(JsonNode? value, JsonObject schema, JsonObject allSchemas, string path)
    {
        var resolved = ResolveSchema(schema, allSchemas);

        // Manejar anyOf / oneOf (p. ej. tipos nulables o referencias).
        var union = (resolved["anyOf"] ?? resolved["oneOf"])?.AsArray();
        if (union is not null)
        {
            if (value is null)
            {
                var allowsNull = union.Any(u => SchemaAllowsNull(ResolveSchema(u!.AsObject(), allSchemas)));
                Assert.True(allowsNull, $"{path}: valor null no permitido por el esquema OpenAPI.");
                return;
            }

            foreach (var branch in union)
            {
                var branchSchema = ResolveSchema(branch!.AsObject(), allSchemas);
                if (IsNullOnlySchema(branchSchema))
                {
                    continue;
                }

                if (TryMatchesSchema(value, branchSchema, allSchemas))
                {
                    return;
                }
            }

            Assert.Fail($"{path}: el valor '{value.ToJsonString()}' no coincide con ninguna rama de anyOf/oneOf.");
        }

        if (value is null)
        {
            Assert.True(SchemaAllowsNull(resolved), $"{path}: valor null no permitido por el esquema OpenAPI.");
            return;
        }

        if (resolved["enum"] is JsonArray enumValues)
        {
            var actual = value.GetValue<string>();
            Assert.Contains(enumValues, e => string.Equals(e?.GetValue<string>(), actual, StringComparison.Ordinal));
            return;
        }

        var types = ExtractTypes(resolved);
        if (types.Count == 0 && resolved["properties"] is JsonObject)
        {
            types.Add("object");
        }

        switch (value)
        {
            case JsonObject obj:
                Assert.Contains("object", types);
                if (resolved["properties"] is JsonObject)
                {
                    ValidateObjectAgainstSchema(obj, resolved, allSchemas, path);
                }

                break;

            case JsonArray arr:
                Assert.Contains("array", types);
                if (resolved["items"] is JsonObject itemSchema)
                {
                    for (var i = 0; i < arr.Count; i++)
                    {
                        ValidateValueAgainstSchema(arr[i], itemSchema, allSchemas, $"{path}[{i}]");
                    }
                }

                break;

            case JsonValue val:
                var kind = val.GetValueKind();
                if (kind == JsonValueKind.String)
                {
                    Assert.Contains("string", types);
                    if (resolved["pattern"]?.GetValue<string>() is { } pattern)
                    {
                        var str = val.GetValue<string>();
                        Assert.True(
                            Regex.IsMatch(str, pattern, RegexOptions.CultureInvariant),
                            $"{path}: la cadena '{str}' no cumple el patrón '{pattern}'.");
                    }
                }
                else if (kind == JsonValueKind.Number)
                {
                    Assert.True(
                        types.Contains("integer") || types.Contains("number"),
                        $"{path}: se esperaba número pero el esquema declara [{string.Join(", ", types)}].");
                    if (types.Contains("integer") && !types.Contains("number"))
                    {
                        var numText = val.ToJsonString();
                        Assert.True(
                            long.TryParse(numText, System.Globalization.CultureInfo.InvariantCulture, out _),
                            $"{path}: se esperaba entero pero el valor serializado es '{numText}'.");
                    }
                }
                else if (kind is JsonValueKind.True or JsonValueKind.False)
                {
                    Assert.Contains("boolean", types);
                }

                break;
        }
    }

    private static bool TryMatchesSchema(JsonNode value, JsonObject schema, JsonObject allSchemas)
    {
        try
        {
            ValidateValueAgainstSchema(value, schema, allSchemas, "$");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SchemaAllowsNull(JsonObject schema)
    {
        if (schema["nullable"]?.GetValue<bool>() == true)
        {
            return true;
        }

        if (schema["enum"] is JsonArray enumValues && enumValues.Any(e => e is null))
        {
            return true;
        }

        return ExtractTypes(schema).Contains("null");
    }

    private static bool IsNullOnlySchema(JsonObject schema)
    {
        var types = ExtractTypes(schema);
        return types.Count == 1 && types[0] == "null";
    }

    private static List<string> ExtractTypes(JsonObject schema)
    {
        var result = new List<string>();
        if (schema["type"] is JsonValue single && single.GetValueKind() == JsonValueKind.String)
        {
            result.Add(single.GetValue<string>());
        }
        else if (schema["type"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item?.GetValue<string>() is { } t)
                {
                    result.Add(t);
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Limpieza de mejor esfuerzo.
        }
    }
}
