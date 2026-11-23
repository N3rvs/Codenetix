using System.Globalization;
using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.CancelRun;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CodeNetix.Pipeline.Features.Budget;

/// <summary>
/// Implementación del libro de presupuesto (<see cref="IBudgetLedger"/>, AD-15):
/// persiste cada cargo en <c>run_budget</c> y emite <see cref="TokensConsumed"/> en el mismo
/// <see cref="RunEventLog.SaveAndPublishAsync"/>, con reintento ante colisión de <c>seq</c>.
/// Cuando el coste acumulado estimable alcanza o supera <c>budget_usd</c>, devuelve
/// <see cref="BudgetChargeOutcome.Exhausted"/> y dispara el corte cooperativo en <see cref="RunCancellationRegistry"/>.
/// </summary>
internal sealed class BudgetLedger(
    IDbContextFactory<PipelineDbContext> contextFactory,
    RunEventLog log,
    IOptionsMonitor<PricingOptions> pricingOptions,
    RunCancellationRegistry? cancellationRegistry = null) : IBudgetLedger
{
    private const int MaxAttempts = 5;

    public async Task<BudgetChargeResult> ChargeAsync(BudgetChargeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var inputTokens = Math.Max(0, request.InputTokens);
        var outputTokens = Math.Max(0, request.OutputTokens);
        if (inputTokens == 0 && outputTokens == 0)
        {
            var current = await GetRunCostAsync(request.RunId, cancellationToken).ConfigureAwait(false);
            var emptyResult = ToChargeResult(chargeUsd: null, current);
            NotifyExhaustedIfNeeded(request.RunId, current, emptyResult);
            return emptyResult;
        }

        var role = string.IsNullOrWhiteSpace(request.Role) ? "unknown" : request.Role.Trim();
        var provider = string.IsNullOrWhiteSpace(request.Provider) ? "unknown" : request.Provider.Trim();
        var model = string.IsNullOrWhiteSpace(request.Model) ? "unknown" : request.Model.Trim();

        var isPriced = ModelPricingCatalog.TryResolve(model, pricingOptions.CurrentValue.Models, out var pricing);
        decimal? chargeUsd = isPriced
            ? ModelPricingCatalog.CalculateChargeUsd(inputTokens, outputTokens, pricing)
            : null;

        decimal? budgetLimitUsd = null;
        for (var attempt = 1; ; attempt++)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var run = await context.Runs
                .AsNoTracking()
                .Where(r => r.Id == request.RunId)
                .Select(r => new { r.Id, r.EffectiveConfigJson })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (run is null)
            {
                throw new InvalidOperationException($"No existe la Ejecución {request.RunId}.");
            }

            budgetLimitUsd = run.EffectiveConfigJson is null
                ? null
                : EffectiveConfigSerializer.Deserialize(run.EffectiveConfigJson).BudgetUsd;

            var now = log.UtcNow;
            context.BudgetCharges.Add(new RunBudgetRecord
            {
                Id = Guid.CreateVersion7(now),
                RunId = request.RunId,
                StageId = request.StageId,
                Role = role,
                Provider = provider,
                Model = model,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CostUsd = chargeUsd,
                IsPriced = isPriced,
                CreatedAt = now,
            });

            await log.AddAsync(
                context,
                request.RunId,
                new TokensConsumed(role, provider, model, inputTokens, outputTokens),
                cancellationToken,
                stageId: request.StageId).ConfigureAwait(false);

            try
            {
                await log.SaveAndPublishAsync(context, cancellationToken).ConfigureAwait(false);
                var summary = await SummarizeForRunAsync(context, request.RunId, budgetLimitUsd, CancellationToken.None).ConfigureAwait(false);
                var result = ToChargeResult(chargeUsd, summary);
                NotifyExhaustedIfNeeded(request.RunId, summary, result);
                return result;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && RunEventLog.IsUniqueViolation(ex))
            {
                // Otro escritor tomó el mismo seq en run_events: se reintenta con un contexto limpio.
            }
        }
    }

    public async Task<RunCostSummary> GetRunCostAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var configJson = await context.Runs
            .AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.EffectiveConfigJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var budgetLimitUsd = configJson is null
            ? null
            : EffectiveConfigSerializer.Deserialize(configJson).BudgetUsd;

        return await SummarizeForRunAsync(context, runId, budgetLimitUsd, cancellationToken).ConfigureAwait(false);
    }

    internal static RunCostSummary Summarize(
        IEnumerable<BudgetChargeProjection> rows,
        decimal? budgetLimitUsd)
    {
        var count = 0;
        long totalInput = 0;
        long totalOutput = 0;
        var totalCost = 0m;
        var hasUnpriced = false;

        foreach (var row in rows)
        {
            count++;
            totalInput += Math.Max(0, row.InputTokens);
            totalOutput += Math.Max(0, row.OutputTokens);
            if (!row.IsPriced || !row.CostUsd.HasValue)
            {
                hasUnpriced = true;
            }
            else
            {
                totalCost += row.CostUsd.Value;
            }
        }

        if (count == 0)
        {
            return new RunCostSummary(
                EstimatedCostUsd: null,
                IsEstimable: false,
                HasUnpricedModels: false,
                TotalInputTokens: 0,
                TotalOutputTokens: 0,
                BudgetLimitUsd: budgetLimitUsd);
        }

        var clampedInput = (int)Math.Min(int.MaxValue, totalInput);
        var clampedOutput = (int)Math.Min(int.MaxValue, totalOutput);

        if (hasUnpriced)
        {
            return new RunCostSummary(
                EstimatedCostUsd: null,
                IsEstimable: false,
                HasUnpricedModels: true,
                TotalInputTokens: clampedInput,
                TotalOutputTokens: clampedOutput,
                BudgetLimitUsd: budgetLimitUsd);
        }

        return new RunCostSummary(
            EstimatedCostUsd: decimal.Round(totalCost, 8, MidpointRounding.AwayFromZero),
            IsEstimable: true,
            HasUnpricedModels: false,
            TotalInputTokens: clampedInput,
            TotalOutputTokens: clampedOutput,
            BudgetLimitUsd: budgetLimitUsd);
    }

    private static async Task<RunCostSummary> SummarizeForRunAsync(
        PipelineDbContext context,
        Guid runId,
        decimal? budgetLimitUsd,
        CancellationToken cancellationToken)
    {
        var rows = await context.BudgetCharges
            .AsNoTracking()
            .Where(b => b.RunId == runId)
            .Select(b => new BudgetChargeProjection(b.RunId, b.InputTokens, b.OutputTokens, b.CostUsd, b.IsPriced))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Summarize(rows, budgetLimitUsd);
    }

    private void NotifyExhaustedIfNeeded(Guid runId, RunCostSummary summary, BudgetChargeResult result)
    {
        if (result.Outcome == BudgetChargeOutcome.Exhausted)
        {
            cancellationRegistry?.TryRequestBudgetExhausted(runId, FormatExhaustedDetail(summary));
        }
    }

    internal static string FormatExhaustedDetail(RunCostSummary summary) =>
        summary is { EstimatedCostUsd: { } spent, BudgetLimitUsd: { } limit }
            ? string.Create(CultureInfo.InvariantCulture, $"presupuesto agotado: ${spent:F4} USD de ${limit:F4} USD")
            : "presupuesto agotado";

    private static BudgetChargeResult ToChargeResult(decimal? chargeUsd, RunCostSummary summary)
    {
        var isExhausted = summary.IsExhausted;
        var outcome = isExhausted ? BudgetChargeOutcome.Exhausted : BudgetChargeOutcome.Recorded;
        var remainingUsd = isExhausted ? 0m : summary.RemainingUsd;

        return new BudgetChargeResult(
            Outcome: outcome,
            ChargeUsd: chargeUsd,
            TotalEstimatedCostUsd: summary.EstimatedCostUsd,
            HasUnpricedModels: summary.HasUnpricedModels,
            BudgetLimitUsd: summary.BudgetLimitUsd,
            RemainingUsd: remainingUsd);
    }

    internal readonly record struct BudgetChargeProjection(
        Guid RunId,
        int InputTokens,
        int OutputTokens,
        decimal? CostUsd,
        bool IsPriced);
}
