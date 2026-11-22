using CodeNetix.Agents.ChatClients;
using CodeNetix.Agents.StructuredOutput;
using CodeNetix.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Agents.Features.AnalyzeIssue;

/// <summary>
/// Analista (FR-9, AD-5): un <see cref="Microsoft.Extensions.AI.IChatClient"/> compatible con OpenAI, sin herramientas,
/// con salida estructurada validada contra <see cref="AnalysisSchema"/> (que referencia <see cref="SpecSchema"/>) y
/// <see cref="AnalystOutput.Rules"/> mediante el <see cref="SchemaRetryDecorator"/>. Devuelve la Spec o el rechazo
/// <see cref="AnalysisResult.NotAutomatable"/> (FR-10). El modelo es <c>models.analyst</c> de la
/// <see cref="EffectiveConfig"/> o, si no lo define, el de la instancia. Cada salida rechazada se publica como
/// <see cref="AnalystOutputRejected"/>.
/// </summary>
internal sealed partial class Analyst(
    IAgentChatClientFactory clientFactory,
    SchemaRetryDecorator retrier,
    IRunEventSink events,
    IOptions<AgentsOptions> options,
    ILogger<Analyst> logger,
    IBudgetLedger? budgetLedger = null) : IAnalyst
{
    public const string RoleName = "Analyst";
    public const string NotConfigured = "no hay ningún Proveedor configurado para el Analista";

    public async Task<AnalysisResult> AnalyzeAsync(AnalysisInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (options.Value.Analyst is not { } endpoint)
        {
            return new AnalysisResult.ProviderError(NotConfigured);
        }

        if (budgetLedger is not null)
        {
            var currentCost = await budgetLedger.GetRunCostAsync(input.RunId, cancellationToken).ConfigureAwait(false);
            if (currentCost.IsExhausted)
            {
                return new AnalysisResult.BudgetExhausted(
                    BudgetTrackingChatClient.FormatExhaustedDetail(currentCost.EstimatedCostUsd, currentCost.BudgetLimitUsd));
            }
        }

        var model = input.Config.Models?.Analyst is { } repoModel && !string.IsNullOrWhiteSpace(repoModel)
            ? repoModel.Trim()
            : endpoint.Model;

        var call = new StructuredCall(AnalystPrompt.System, AnalystPrompt.User(input), endpoint.StructuredOutput);
        LogAnalyzing(RunShortId.Of(input.RunId), endpoint.Provider, model, endpoint.StructuredOutput);

        BudgetTrackingChatClient client;
        try
        {
            var rawClient = clientFactory.Create(endpoint, model);
            client = new BudgetTrackingChatClient(rawClient, budgetLedger, input.RunId, stageId: null, RoleName, endpoint.Provider, model, logger);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            // Solo la construcción del cliente (URL o modelo inválidos) es un fallo del Proveedor; los errores del
            // sumidero o de la BD se propagan (el Dispatcher cierra la Ejecución como error interno).
            return new AnalysisResult.ProviderError(SchemaRetryDecorator.DescribeProviderError(ex));
        }

        StructuredOutcome<AnalystOutput> outcome;
        using (client)
        {
            outcome = await retrier.RunAsync(
                client,
                model,
                call,
                AnalystOutput.Validator,
                (attempt, error, ct) => events.AppendAsync(input.RunId, null, new AnalystOutputRejected(attempt, error), ct),
                cancellationToken).ConfigureAwait(false);
        }

        if (client.BudgetExhausted)
        {
            return new AnalysisResult.BudgetExhausted(client.BudgetExhaustedDetail ?? "presupuesto agotado");
        }

        return outcome switch
        {
            StructuredOutcome<AnalystOutput>.Success ok => ToResult(ok.Value, ok.Attempts, model),
            StructuredOutcome<AnalystOutput>.Invalid invalid => new AnalysisResult.SchemaInvalid(invalid.LastError, invalid.Attempts),
            StructuredOutcome<AnalystOutput>.ProviderFailed failed => new AnalysisResult.ProviderError(failed.Detail),
            StructuredOutcome<AnalystOutput>.BudgetExhausted exhausted => new AnalysisResult.BudgetExhausted(exhausted.Detail),
            _ => throw new InvalidOperationException("Resultado de salida estructurada desconocido."),
        };
    }

    /// <summary>Una salida ya validada (esquema y <see cref="AnalystOutput.Rules"/>) → <see cref="AnalysisResult"/>.</summary>
    private static AnalysisResult ToResult(AnalystOutput output, int attempts, string model)
    {
        if (output.Outcome == AnalysisSchema.OutcomeSpec)
        {
            return new AnalysisResult.Ok(output.Spec!, attempts, model);
        }

        NotAutomatableCategories.TryParse(output.Category, out var category);
        return new AnalysisResult.NotAutomatable(category, output.Explanation!.Trim(), attempts, model);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {ShortId}: el Analista consulta {Provider} con el modelo {Model} (salida estructurada: {StructuredOutput}).")]
    private partial void LogAnalyzing(string shortId, string provider, string model, bool structuredOutput);
}
