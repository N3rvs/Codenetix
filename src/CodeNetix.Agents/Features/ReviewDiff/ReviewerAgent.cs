using CodeNetix.Agents.ChatClients;
using CodeNetix.Agents.StructuredOutput;
using CodeNetix.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Agents.Features.ReviewDiff;

/// <summary>
/// Revisor independiente (FR-14, FR-15, AD-5): usa <see cref="IAgentChatClientFactory"/> y
/// <see cref="SchemaRetryDecorator"/> con <c>schemas/verdict.v1.json</c>. Cada especialidad puede usar un Proveedor o
/// modelo distinto compatible con OpenAI. Si agota los reintentos de esquema o sufre un error de proveedor, devuelve
/// <see cref="VerdictDecision.ChangesRequested"/> con el detalle del error en <see cref="Verdict.Summary"/> sin
/// lanzar excepción no controlada.
/// </summary>
internal sealed partial class ReviewerAgent(
    IAgentChatClientFactory clientFactory,
    SchemaRetryDecorator retrier,
    IRunEventSink events,
    IOptions<AgentsOptions> options,
    ILogger<ReviewerAgent> logger,
    IBudgetLedger? budgetLedger = null) : IReviewerAgent
{
    public async Task<ReviewResult> ReviewAsync(ReviewInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var settings = options.Value;
        var endpoint = settings.ResolveReviewerEndpoint(input.ReviewerKind);
        var shortId = RunShortId.Of(input.RunId);
        if (endpoint is null)
        {
            var summary = $"Error del Proveedor: no hay ningún Proveedor configurado para el Revisor {input.ReviewerKind}.";
            LogReviewFallback(shortId, input.Round, input.ReviewerKind, summary);
            return new ReviewResult(new Verdict(VerdictDecision.ChangesRequested, summary, []), Attempts: 0, Model: "unconfigured", FallbackDueToError: true);
        }

        var hasSpecialtyOverride = settings.ReviewerOverrides.ContainsKey(input.ReviewerKind);
        var model = !hasSpecialtyOverride && input.Config.Models?.Reviewers is { } repoModel && !string.IsNullOrWhiteSpace(repoModel)
            ? repoModel.Trim()
            : endpoint.Model;

        var call = new StructuredCall(
            ReviewerPrompt.SystemFor(input.ReviewerKind),
            ReviewerPrompt.User(input),
            endpoint.StructuredOutput);

        LogReviewing(shortId, input.Round, input.ReviewerKind, endpoint.Provider, model, endpoint.StructuredOutput);

        Microsoft.Extensions.AI.IChatClient client;
        try
        {
            var rawClient = clientFactory.Create(endpoint, model);
            var role = $"Reviewer:{input.ReviewerKind.ToString().ToLowerInvariant()}";
            client = new BudgetTrackingChatClient(rawClient, budgetLedger, input.RunId, input.StageId, role, endpoint.Provider, model, logger);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            var detail = SchemaRetryDecorator.DescribeProviderError(ex);
            var summary = $"Error del Proveedor: {detail}";
            LogReviewFallback(shortId, input.Round, input.ReviewerKind, summary);
            return new ReviewResult(
                new Verdict(VerdictDecision.ChangesRequested, summary, []),
                Attempts: 1,
                Model: model,
                FallbackDueToError: true);
        }

        var rejectedAttempts = 0;
        StructuredOutcome<Verdict> outcome;
        using (client)
        {
            outcome = await retrier.RunAsync(
                client,
                model,
                call,
                VerdictValidator.Validator,
                (attempt, error, ct) =>
                {
                    rejectedAttempts++;
                    return events.AppendAsync(
                        input.RunId,
                        input.StageId,
                        new ReviewerOutputRejected(input.Round, input.ReviewerKind, attempt, error),
                        ct);
                },
                cancellationToken).ConfigureAwait(false);
        }

        switch (outcome)
        {
            case StructuredOutcome<Verdict>.Success ok:
                return new ReviewResult(ok.Value, ok.Attempts, model);

            case StructuredOutcome<Verdict>.Invalid invalid:
            {
                var summary = $"Veredicto inválido tras {invalid.Attempts} intento(s): {invalid.LastError}";
                LogReviewFallback(shortId, input.Round, input.ReviewerKind, summary);
                return new ReviewResult(
                    new Verdict(VerdictDecision.ChangesRequested, summary, []),
                    invalid.Attempts,
                    model,
                    FallbackDueToError: true);
            }

            case StructuredOutcome<Verdict>.ProviderFailed failed:
            {
                var summary = $"Error del Proveedor: {failed.Detail}";
                LogReviewFallback(shortId, input.Round, input.ReviewerKind, summary);
                return new ReviewResult(
                    new Verdict(VerdictDecision.ChangesRequested, summary, []),
                    Attempts: rejectedAttempts + 1,
                    model,
                    FallbackDueToError: true);
            }

            default:
                throw new InvalidOperationException("Resultado de salida estructurada desconocido.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {ShortId} (ronda {Round}): el Revisor {Reviewer} consulta {Provider} con el modelo {Model} (salida estructurada: {StructuredOutput}).")]
    private partial void LogReviewing(string shortId, int round, ReviewerKind reviewer, string provider, string model, bool structuredOutput);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ejecución {ShortId} (ronda {Round}): el Revisor {Reviewer} recurre a ChangesRequested por error ({Reason}).")]
    private partial void LogReviewFallback(string shortId, int round, ReviewerKind reviewer, string reason);
}
