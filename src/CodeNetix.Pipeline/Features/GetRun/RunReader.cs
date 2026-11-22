using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using CodeNetix.Pipeline.Features.Budget;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Pipeline.Features.GetRun;

/// <summary>Consultas de lectura de <c>run_runs</c>, <c>run_events</c>, <c>run_specs</c> y <c>run_budget</c> (<c>GET /api/runs/…</c>).</summary>
internal sealed class RunReader(IDbContextFactory<PipelineDbContext> contextFactory) : IRunReader
{
    public async Task<IReadOnlyList<RunSummary>> ListRecentAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.Runs
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return [];
        }

        var runIds = rows.Select(r => r.Id).ToArray();
        var charges = await context.BudgetCharges
            .AsNoTracking()
            .Where(b => runIds.Contains(b.RunId))
            .Select(b => new BudgetLedger.BudgetChargeProjection(b.RunId, b.InputTokens, b.OutputTokens, b.CostUsd, b.IsPriced))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var chargesByRun = charges.ToLookup(c => c.RunId);
        return
        [
            .. rows.Select(r => new RunSummary(
                r.Id,
                r.ShortId,
                r.State,
                r.FailureReason,
                r.FailureDetail,
                r.EventId,
                r.ObjectKey,
                r.CreatedAt,
                r.UpdatedAt,
                BudgetLedger.Summarize(chargesByRun[r.Id], r.GetEffectiveConfig()?.BudgetUsd))),
        ];
    }

    public async Task<RunSummary?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var trimmed = id?.Trim() ?? string.Empty;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        IQueryable<PipelineRun> query;
        if (Guid.TryParse(trimmed, out var runId))
        {
            query = context.Runs.Where(r => r.Id == runId);
        }
        else if (IsShortId(trimmed))
        {
            var shortId = trimmed.ToLowerInvariant();
            query = context.Runs.Where(r => r.ShortId == shortId);
        }
        else
        {
            return null;
        }

        var run = await query.AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        var charges = await context.BudgetCharges
            .AsNoTracking()
            .Where(b => b.RunId == run.Id)
            .Select(b => new BudgetLedger.BudgetChargeProjection(b.RunId, b.InputTokens, b.OutputTokens, b.CostUsd, b.IsPriced))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cost = BudgetLedger.Summarize(charges, run.GetEffectiveConfig()?.BudgetUsd);
        return new RunSummary(run.Id, run.ShortId, run.State, run.FailureReason, run.FailureDetail, run.EventId, run.ObjectKey, run.CreatedAt, run.UpdatedAt, cost);
    }

    public async Task<IReadOnlyList<RunEvent>> GetEventsAsync(Guid runId, long afterSeq, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.Events
            .AsNoTracking()
            .Where(e => e.RunId == runId && e.Seq > afterSeq)
            .OrderBy(e => e.Seq)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => RunEventJson.Deserialize(r.PayloadJson, r.RunId, r.Seq, r.At, r.StageId))];
    }

    public async Task<Spec?> GetSpecAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await context.Specs
            .AsNoTracking()
            .Where(s => s.RunId == runId)
            .Select(s => s.SpecJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return json is null ? null : SpecSerializer.Deserialize(json);
    }

    public async Task<TestReport?> GetTestReportAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await context.TestReports
            .AsNoTracking()
            .Where(t => t.RunId == runId)
            .Select(t => t.ReportJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return json is null ? null : TestReportSerializer.Deserialize(json);
    }

    public async Task<IReadOnlyList<RunVerdict>> GetVerdictsAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.Verdicts
            .AsNoTracking()
            .Where(v => v.RunId == runId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows
                .OrderBy(v => v.Round)
                .ThenBy(v => v.Reviewer)
                .Select(v => new RunVerdict(
                    v.StageId,
                    v.Round,
                    v.Reviewer,
                    v.Decision,
                    v.Summary,
                    VerdictFindingsSerializer.Deserialize(v.FindingsJson),
                    v.Model,
                    v.Attempts,
                    v.CreatedAt)),
        ];
    }

    private static bool IsShortId(string value) =>
        value.Length == 8 && value.All(c => char.IsAsciiHexDigit(c));
}
