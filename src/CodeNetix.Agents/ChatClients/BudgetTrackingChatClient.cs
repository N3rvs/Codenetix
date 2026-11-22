using System.Runtime.CompilerServices;
using CodeNetix.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeNetix.Agents.ChatClients;

/// <summary>
/// Middleware obligatorio sobre <see cref="IChatClient"/> (AD-15) que intercepta cada llamada del Analista y de
/// los Revisores y registra el consumo de tokens en <see cref="IBudgetLedger"/>.
/// </summary>
internal sealed partial class BudgetTrackingChatClient(
    IChatClient innerClient,
    IBudgetLedger? budgetLedger,
    Guid runId,
    Guid? stageId,
    string role,
    string provider,
    string model,
    ILogger? logger = null) : DelegatingChatClient(innerClient)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        await RecordUsageAsync(response.Usage, response.ModelId).ConfigureAwait(false);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long inputTokens = 0;
        long outputTokens = 0;
        string? effectiveModel = null;

        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(update.ModelId))
                {
                    effectiveModel = update.ModelId;
                }

                foreach (var content in update.Contents)
                {
                    if (content is UsageContent { Details: { } details })
                    {
                        if (details.InputTokenCount is > 0)
                        {
                            inputTokens = Math.Max(inputTokens, details.InputTokenCount.Value);
                        }

                        if (details.OutputTokenCount is > 0)
                        {
                            outputTokens = Math.Max(outputTokens, details.OutputTokenCount.Value);
                        }
                    }
                }

                yield return update;
            }
        }
        finally
        {
            if (inputTokens > 0 || outputTokens > 0)
            {
                await RecordUsageAsync(
                    new UsageDetails
                    {
                        InputTokenCount = inputTokens,
                        OutputTokenCount = outputTokens,
                    },
                    effectiveModel).ConfigureAwait(false);
            }
        }
    }

    private async Task RecordUsageAsync(UsageDetails? usage, string? responseModel)
    {
        if (budgetLedger is null || usage is null)
        {
            return;
        }

        var inputTokens = ClampToInt(usage.InputTokenCount);
        var outputTokens = ClampToInt(usage.OutputTokenCount);
        if (inputTokens <= 0 && outputTokens <= 0)
        {
            return;
        }

        var effectiveModel = !string.IsNullOrWhiteSpace(responseModel) ? responseModel.Trim() : model;
        try
        {
            // Se usa CancellationToken.None para no perder el registro de una llamada LLM ya consumida
            // si el timeout de la llamada al proveedor expira justo al terminar la respuesta.
            await budgetLedger.ChargeAsync(
                new BudgetChargeRequest(runId, stageId, role, provider, effectiveModel, inputTokens, outputTokens),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogBudgetChargeFailed(_logger, RunShortId.Of(runId), role, effectiveModel, ex.Message);
        }
    }

    private static int ClampToInt(long? value)
    {
        if (value is null or <= 0)
        {
            return 0;
        }

        return value.Value > int.MaxValue ? int.MaxValue : (int)value.Value;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido registrar el consumo de tokens de la Ejecución {ShortId} ({Role}, modelo {Model}): {Reason}.")]
    private static partial void LogBudgetChargeFailed(ILogger logger, string shortId, string role, string model, string reason);
}
