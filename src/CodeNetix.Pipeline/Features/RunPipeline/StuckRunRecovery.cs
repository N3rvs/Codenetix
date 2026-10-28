using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Events;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Pipeline.Features.RunPipeline;

/// <summary>
/// Red de seguridad del orquestador: evita que una Ejecución quede no terminal (y su objeto bloqueado) cuando
/// la orquestación lanza una excepción o el proceso cae entre crear la Ejecución y orquestarla.
/// </summary>
internal sealed class StuckRunRecovery(IDbContextFactory<PipelineDbContext> contextFactory, RunEventLog log)
{
    /// <summary>Longitud máxima del motivo que se guarda en el detalle del fallo.</summary>
    public const int MaxReasonLength = 200;

    public const string DetailPrefix = "Error interno de orquestación: ";

    /// <summary>Ejecuciones en <c>Received</c>: creadas pero nunca orquestadas.</summary>
    public async Task<IReadOnlyList<Guid>> GetReceivedRunIdsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Runs
            .Where(r => r.State == RunState.Received)
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// En un contexto nuevo: si la Ejecución sigue no terminal, <c>Fail(StageError, detalle)</c> con su
    /// <c>StateChanged</c> en el mismo <c>SaveChanges</c>. El detalle se redacta al persistirse.
    /// </summary>
    public async Task FailIfActiveAsync(Guid runId, Exception error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await context.Runs.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.State.IsTerminal())
        {
            return;
        }

        var change = run.Fail(FailureReason.StageError, DetailPrefix + ShortReason(error));
        await log.AddTransitionAsync(context, run, change, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Mensaje de la excepción en una línea y como mucho <see cref="MaxReasonLength"/> caracteres.</summary>
    public static string ShortReason(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = string.Join(' ', error.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (message.Length == 0)
        {
            message = error.GetType().Name;
        }

        return message.Length <= MaxReasonLength ? message : string.Concat(message.AsSpan(0, MaxReasonLength - 1), "…");
    }
}
