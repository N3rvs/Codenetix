using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using CodeNetix.Pipeline.Events;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Pipeline.Features.GetRun;

/// <summary>Consultas de lectura de <c>run_runs</c>, <c>run_events</c> y <c>run_specs</c> (<c>GET /api/runs/…</c>).</summary>
internal sealed class RunReader(IDbContextFactory<PipelineDbContext> contextFactory) : IRunReader
{
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
        return run is null
            ? null
            : new RunSummary(run.Id, run.ShortId, run.State, run.FailureReason, run.FailureDetail, run.EventId, run.ObjectKey, run.CreatedAt, run.UpdatedAt);
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

        return [.. rows.Select(r => RunEventJson.Deserialize(r.PayloadJson, r.RunId, r.Seq, r.At))];
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

    private static bool IsShortId(string value) =>
        value.Length == 8 && value.All(c => char.IsAsciiHexDigit(c));
}
