using CodeNetix.Agents;
using CodeNetix.Agents.StructuredOutput;
using CodeNetix.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

/// <summary>Decorador común de reintentos de esquema con un <see cref="FakeChatClient"/>, y sus piezas (extracción y reglas).</summary>
public sealed class SchemaRetryDecoratorTests
{
    private const string IssueText = "TEXTO-SECRETO-DE-LA-ISSUE";
    private const string MissingProblem =
        """{"probableCause":"c","proposedChange":"p","acceptanceCriteria":["a"],"affectedFiles":[]}""";

    private static readonly StructuredOutputValidator<Spec> Validator = new(SpecSchema.Json, SpecSchema.Name, SpecRules.Validate);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SchemaRetryDecorator Decorator(int retries = 2, TimeSpan? timeout = null) =>
        new(Options.Create(new AgentsOptions { SchemaRetries = retries, CallTimeout = timeout ?? AgentsOptions.DefaultCallTimeout }));

    private static StructuredCall Call(bool structured = true) =>
        new("You are a test system prompt.", "Issue: " + IssueText, structured);

    private static async Task<(StructuredOutcome<Spec> Outcome, List<(int Attempt, string Error)> Rejections)> RunAsync(
        FakeChatClient client, int retries = 2, bool structured = true, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        var rejections = new List<(int, string)>();
        var outcome = await Decorator(retries, timeout).RunAsync(
            client,
            "model-x",
            Call(structured),
            Validator,
            (attempt, error, _) =>
            {
                rejections.Add((attempt, error));
                return Task.CompletedTask;
            },
            cancellationToken ?? Ct);
        return (outcome, rejections);
    }

    [Fact]
    public async Task Valid_first_answer_succeeds_with_one_attempt_and_a_json_schema_request()
    {
        var client = new FakeChatClient(FakeChatClient.ValidSpecJson);

        var (outcome, rejections) = await RunAsync(client);

        var success = Assert.IsType<StructuredOutcome<Spec>.Success>(outcome);
        Assert.Equal(1, success.Attempts);
        Assert.Equal("src/ProfileController.cs", Assert.Single(success.Value.AffectedFiles));
        Assert.Empty(rejections);

        var call = Assert.Single(client.Calls);
        Assert.Equal([ChatRole.System, ChatRole.User], call.Messages.Select(m => m.Role));
        Assert.Equal("model-x", call.Options!.ModelId);
        Assert.Null(call.Options.Tools);
        var format = Assert.IsType<ChatResponseFormatJson>(call.Options.ResponseFormat);
        Assert.Equal(SpecSchema.Name, format.SchemaName);
        Assert.Equal("object", format.Schema!.Value.GetProperty("type").GetString());
        Assert.False(format.Schema.Value.TryGetProperty("$schema", out _));
        // Anthropic rechaza json_schema sin "strict" (HTTP 400): se envía siempre explícito.
        Assert.Equal(false, call.Options.AdditionalProperties?["strict"]);
    }

    [Fact]
    public async Task Invalid_then_valid_retries_with_the_validation_error_but_never_the_issue()
    {
        var client = new FakeChatClient(FakeChatClient.ValidSpecJson).Respond(MissingProblem);

        var (outcome, rejections) = await RunAsync(client);

        Assert.Equal(2, Assert.IsType<StructuredOutcome<Spec>.Success>(outcome).Attempts);
        var (attempt, error) = Assert.Single(rejections);
        Assert.Equal(1, attempt);
        Assert.Contains("'problem'", error, StringComparison.Ordinal);

        Assert.Equal(2, client.Calls.Count);
        var retry = client.Calls[1].Messages;
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.User], retry.Select(m => m.Role));
        Assert.Contains(error, retry[2].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(IssueText, retry[2].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(IssueText, retry[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Always_invalid_exhausts_one_plus_N_attempts_and_reports_the_last_error()
    {
        var client = new FakeChatClient(FakeChatClient.ValidSpecJson).Respond("no json at all", MissingProblem, """{"problem":"x"}""");

        var (outcome, rejections) = await RunAsync(client, retries: 2);

        var invalid = Assert.IsType<StructuredOutcome<Spec>.Invalid>(outcome);
        Assert.Equal(3, invalid.Attempts);
        Assert.Equal([1, 2, 3], rejections.Select(r => r.Attempt));
        Assert.Contains("ningún objeto JSON", rejections[0].Error, StringComparison.Ordinal);
        Assert.Equal(rejections[2].Error, invalid.LastError);
        Assert.Contains("'probableCause'", invalid.LastError, StringComparison.Ordinal);
        Assert.Equal(3, client.Calls.Count);
    }

    [Fact]
    public async Task Zero_retries_means_a_single_attempt()
    {
        var client = new FakeChatClient(FakeChatClient.ValidSpecJson).Respond(MissingProblem);

        var (outcome, _) = await RunAsync(client, retries: 0);

        Assert.Equal(1, Assert.IsType<StructuredOutcome<Spec>.Invalid>(outcome).Attempts);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task Dangerous_path_is_rejected_semantically_and_retried()
    {
        var client = new FakeChatClient(FakeChatClient.ValidSpecJson).Respond(
            """{"problem":"p","probableCause":"c","proposedChange":"x","acceptanceCriteria":["a"],"affectedFiles":["../x"]}""");

        var (outcome, rejections) = await RunAsync(client);

        Assert.Equal(2, Assert.IsType<StructuredOutcome<Spec>.Success>(outcome).Attempts);
        Assert.Contains("affectedFiles[0]", Assert.Single(rejections).Error, StringComparison.Ordinal);
        Assert.DoesNotContain("../x", rejections[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_error_ends_immediately_without_consuming_schema_retries()
    {
        var client = new FakeChatClient(FakeChatClient.ValidSpecJson)
            .Respond(MissingProblem)
            .Throw(new HttpRequestException("boom", null, System.Net.HttpStatusCode.InternalServerError));

        var (outcome, rejections) = await RunAsync(client);

        var failed = Assert.IsType<StructuredOutcome<Spec>.ProviderFailed>(outcome);
        Assert.Equal("HTTP 500", failed.Detail);
        Assert.Single(rejections);
        Assert.Equal(2, client.Calls.Count);
    }

    [Fact]
    public async Task Call_timeout_is_a_provider_error()
    {
        var client = new FakeChatClient(FakeChatClient.ValidSpecJson).Hang();

        var (outcome, rejections) = await RunAsync(client, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Contains("sin respuesta", Assert.IsType<StructuredOutcome<Spec>.ProviderFailed>(outcome).Detail, StringComparison.Ordinal);
        Assert.Empty(rejections);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        source.CancelAfter(TimeSpan.FromMilliseconds(50));
        var client = new FakeChatClient(FakeChatClient.ValidSpecJson).Hang();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(client, cancellationToken: source.Token));
    }

    [Fact]
    public async Task Without_structured_output_the_schema_goes_in_the_system_prompt_and_a_json_fence_is_accepted()
    {
        var client = new FakeChatClient(FakeChatClient.ValidSpecJson).Respond("Sure! Here is the spec:\n```json\n" + FakeChatClient.ValidSpecJson + "\n```\nAnything else?");

        var (outcome, _) = await RunAsync(client, structured: false);

        Assert.Equal(1, Assert.IsType<StructuredOutcome<Spec>.Success>(outcome).Attempts);
        var call = Assert.Single(client.Calls);
        Assert.Null(call.Options!.ResponseFormat);
        Assert.Null(call.Options.AdditionalProperties);
        Assert.Contains("\"acceptanceCriteria\"", call.Messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("JSON Schema", call.Messages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(IssueText, call.Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pretty_printed_answer_with_a_fence_inside_a_string_is_accepted()
    {
        const string Answer =
            """
            {
              "problem": "The README shows:\n```json\n{\"broken\": true}\n```\nand nothing works.",
              "probableCause": "c",
              "proposedChange": "Use ``` fences { carefully }.",
              "acceptanceCriteria": ["a"],
              "affectedFiles": ["README.md"]
            }
            """;
        var client = new FakeChatClient(FakeChatClient.ValidSpecJson).Respond(Answer);

        var (outcome, rejections) = await RunAsync(client, structured: false);

        var success = Assert.IsType<StructuredOutcome<Spec>.Success>(outcome);
        Assert.StartsWith("The README shows:", success.Value.Problem, StringComparison.Ordinal);
        Assert.Empty(rejections);
    }

    [Theory]
    [InlineData("IgnorePreviousInstructions")]
    [InlineData("evil\u0007\nname with spaces and a very long tail that goes on and on and on")]
    public void Extra_property_errors_cite_only_the_parent_object(string name)
    {
        var json = $$"""{"problem":"p","probableCause":"c","proposedChange":"x","acceptanceCriteria":["a"],"affectedFiles":[],{{System.Text.Json.JsonSerializer.Serialize(name)}}:1}""";

        var error = Validator.Validate(json).Error;

        Assert.Equal("$: propiedad no admitida", error);
    }

    [Fact]
    public void Validation_errors_have_no_control_characters_and_are_bounded()
    {
        var error = StructuredOutputValidator<Spec>.Sanitize("a\u0000b\nc" + new string('x', 2000));

        Assert.DoesNotContain(error, char.IsControl);
        Assert.Equal(StructuredOutputValidator<Spec>.MaxErrorLength, error.Length);
    }

    [Fact]
    public void Oversized_answers_and_oversized_fields_are_invalid()
    {
        Assert.Contains("supera", Validator.Validate(new string(' ', StructuredOutputValidator<Spec>.MaxResponseLength + 1)).Error, StringComparison.Ordinal);

        var longPath = new string('a', 301);
        var error = Validator.Validate($$"""{"problem":"p","probableCause":"c","proposedChange":"x","acceptanceCriteria":["a"],"affectedFiles":["{{longPath}}"]}""").Error;
        Assert.Equal("$.affectedFiles[0]: es demasiado largo", error);
    }

    [Fact]
    public async Task Requests_cap_the_output_tokens()
    {
        var client = new FakeChatClient(FakeChatClient.ValidSpecJson);

        await RunAsync(client);

        Assert.Equal(SchemaRetryDecorator.MaxOutputTokens, Assert.Single(client.Calls).Options!.MaxOutputTokens);
    }

    [Fact]
    public void The_public_spec_schema_file_is_the_embedded_one()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "CodeNetix.slnx")))
        {
            root = root.Parent ?? throw new InvalidOperationException("No se encuentra CodeNetix.slnx.");
        }

        var onDisk = File.ReadAllText(Path.Combine(root.FullName, "schemas", "spec.v1.json"));

        Assert.Equal(onDisk, SpecSchema.Json);
        Assert.Contains("\"additionalProperties\": false", onDisk, StringComparison.Ordinal);
        Assert.Contains("https://json-schema.org/draft/2020-12/schema", onDisk, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"a":1}""", """{"a":1}""")]
    [InlineData("Texto {\"a\":\"}{\"} y más", """{"a":"}{"}""")]
    [InlineData("```json\n{\"a\":{\"b\":2}}\n```", """{"a":{"b":2}}""")]
    [InlineData("intro {\"x\":1}\n```json\n{\"y\":2}\n```", """{"y":2}""")]
    [InlineData("```\n{\"a\":\"\\\"}\"}\n```", """{"a":"\"}"}""")]
    [InlineData("sin objeto", null)]
    [InlineData("{ abierto", null)]
    [InlineData("{no es json} y luego {\"a\":1}", """{"a":1}""")]
    [InlineData("{ sin cerrar {\"a\":1}", """{"a":1}""")]
    [InlineData("```json\n{roto}\n```\n{\"b\":2}", """{"b":2}""")]
    [InlineData("  {\n  \"a\": \"```\"\n}  ", "{\n  \"a\": \"```\"\n}")]
    public void First_json_object_is_extracted(string text, string? expected) =>
        Assert.Equal(expected, JsonObjectExtractor.ExtractFirstObject(text));

    [Theory]
    [InlineData("src/App.cs", true)]
    [InlineData("README.md", true)]
    [InlineData("a/b..c/d.cs", true)]
    [InlineData("../x", false)]
    [InlineData("src/../../etc/passwd", false)]
    [InlineData("src\\..\\x", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("\\server\\share", false)]
    [InlineData("C:/Windows/x", false)]
    [InlineData("~/x", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(" src/App.cs", false)]
    [InlineData("src/App.cs ", false)]
    [InlineData("a/./b", false)]
    [InlineData("a//b", false)]
    [InlineData("a/.. /b", false)]
    [InlineData("a/.../b", false)]
    [InlineData(".git/config", false)]
    [InlineData("sub/.GIT/hooks/pre-commit", false)]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData(".editorconfig", true)]
    public void Affected_files_must_be_safe_relative_paths(string path, bool safe) =>
        Assert.Equal(safe, SpecRules.IsSafeRelativePath(path));
}
