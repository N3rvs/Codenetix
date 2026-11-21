using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodeNetix.Contracts;
using CodeNetix.Pipeline.Data;
using CodeNetix.Pipeline.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodeNetix.Pipeline.Events;

/// <summary>
/// Sumidero interno (AD-3): añade un <see cref="RunEvent"/> al <see cref="PipelineDbContext"/> que se le pasa,
/// sin guardar, con <c>seq</c> = máximo (persistido o pendiente en el contexto) + 1. Así la transición y su
/// evento se confirman en el mismo <c>SaveChanges</c>. El JSON pasa por <see cref="ISecretRedactor"/>.
/// </summary>
internal sealed class RunEventLog(ISecretRedactor redactor, TimeProvider timeProvider)
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    internal ISecretRedactor Redactor => redactor;

    /// <summary>
    /// Registra la transición de <paramref name="run"/>: actualiza <c>updated_at</c>, redacta el detalle del
    /// fallo y añade <paramref name="change"/> al contexto.
    /// </summary>
    public Task<RunEvent> AddTransitionAsync(PipelineDbContext context, PipelineRun run, StateChanged change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(change);

        run.Touch(UtcNow);
        run.RedactFailureDetail(redactor);
        return AddAsync(context, run.Id, change, cancellationToken);
    }

    /// <summary>Añade <paramref name="payload"/> al contexto (no guarda) y lo devuelve con <c>Seq</c>, <c>RunId</c>, <c>StageId</c> y <c>At</c>.</summary>
    public async Task<RunEvent> AddAsync(PipelineDbContext context, Guid runId, RunEvent payload, CancellationToken cancellationToken, Guid? stageId = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);

        var persistedMax = await context.Events
            .Where(e => e.RunId == runId)
            .MaxAsync(e => (long?)e.Seq, cancellationToken)
            .ConfigureAwait(false) ?? 0;

        var pendingMax = context.ChangeTracker.Entries<RunEventRecord>()
            .Where(e => e.State == EntityState.Added && e.Entity.RunId == runId)
            .Select(e => (long?)e.Entity.Seq)
            .Max() ?? 0;

        var effectiveStageId = stageId ?? payload.StageId;
        var stamped = payload with
        {
            Seq = Math.Max(persistedMax, pendingMax) + 1,
            RunId = runId,
            StageId = effectiveStageId,
            At = UtcNow,
        };

        var json = redactor.Redact(RunEventJson.Serialize(stamped));
        context.Events.Add(new RunEventRecord
        {
            RunId = runId,
            Seq = stamped.Seq,
            StageId = effectiveStageId,
            Type = RunEventJson.TypeOf(stamped),
            PayloadJson = json,
            At = stamped.At,
        });

        // Se devuelve lo que se persiste (ya redactado).
        return RunEventJson.Deserialize(json, stamped.RunId, stamped.Seq, stamped.At, effectiveStageId);
    }

    private const int SqliteConstraint = 19;

    /// <summary>Violación de UNIQUE o de clave primaria en SQLite.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint, SqliteExtendedErrorCode: 2067 or 1555 };
}

/// <summary>Serialización de la unión <see cref="RunEvent"/> (JSON web, camelCase, discriminador <c>type</c>).</summary>
internal static class RunEventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(RunEvent runEvent) => JsonSerializer.Serialize(runEvent, Options);

    public static RunEvent Deserialize(string json, Guid runId, long seq, DateTimeOffset at, Guid? stageId = null)
    {
        var parsed = JsonSerializer.Deserialize<RunEvent>(json, Options)
            ?? throw new JsonException("RunEvent nulo.");
        return parsed with { RunId = runId, Seq = seq, StageId = stageId ?? parsed.StageId, At = at };
    }

    public static string TypeOf(RunEvent runEvent) => runEvent.GetType().Name;
}

/// <summary>
/// <see cref="IRunEventSink"/> para productores externos a Pipeline: cada llamada usa su propio contexto y
/// confirma el evento. Las transiciones (<see cref="RunCreated"/>, <see cref="StateChanged"/>) solo las emite
/// el agregado, así que aquí se rechazan.
/// </summary>
internal sealed class RunEventSink(
    IDbContextFactory<PipelineDbContext> contextFactory,
    RunEventLog log,
    ISecretRedactor? redactor = null) : IRunEventSink
{
    private const int MaxAttempts = 5;
    private readonly ISecretRedactor _redactor = redactor ?? log.Redactor;
    private readonly ConcurrentDictionary<(Guid RunId, Guid StageId), StageOutputTracker> _stageTrackers = new();

    private sealed class StageOutputTracker
    {
        public long TotalBytes;
        public bool Truncated;
    }

    public async Task<RunEvent> AppendAsync(Guid runId, Guid? stageId, RunEvent payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload is RunCreated or StateChanged)
        {
            throw new ArgumentException($"{RunEventJson.TypeOf(payload)} solo lo emite el agregado PipelineRun.", nameof(payload));
        }

        if (payload is OutputAppended appended)
        {
            var stageKey = (runId, stageId ?? Guid.Empty);
            var tracker = _stageTrackers.GetOrAdd(stageKey, _ => new StageOutputTracker());

            string? textToPersist = null;
            var shouldTruncate = false;
            lock (tracker)
            {
                if (tracker.Truncated)
                {
                    return appended with { RunId = runId };
                }

                var redacted = _redactor.Redact(appended.Text);
                var utf8Bytes = Encoding.UTF8.GetByteCount(redacted);
                var maxBytes = OutputTruncated.DefaultMaxBytes;

                if (tracker.TotalBytes + utf8Bytes <= maxBytes)
                {
                    tracker.TotalBytes += utf8Bytes;
                    textToPersist = redacted;
                }
                else
                {
                    var allowedBytes = (int)Math.Max(0, maxBytes - tracker.TotalBytes);
                    var (cutText, cutBytes) = TruncateToUtf8Bytes(redacted, allowedBytes);
                    tracker.TotalBytes += cutBytes;
                    tracker.Truncated = true;
                    shouldTruncate = true;
                    if (cutText.Length > 0)
                    {
                        textToPersist = cutText;
                    }
                }
            }

            RunEvent? result = null;
            if (textToPersist is not null)
            {
                result = await AppendCoreAsync(runId, stageId, appended with { Text = textToPersist }, cancellationToken).ConfigureAwait(false);
            }

            if (shouldTruncate)
            {
                var truncResult = await AppendCoreAsync(runId, stageId, new OutputTruncated(), cancellationToken).ConfigureAwait(false);
                result ??= truncResult;
            }

            return result ?? appended with { RunId = runId };
        }

        if (payload is OutputTruncated)
        {
            var stageKey = (runId, stageId ?? Guid.Empty);
            var tracker = _stageTrackers.GetOrAdd(stageKey, _ => new StageOutputTracker());
            lock (tracker)
            {
                tracker.Truncated = true;
            }
        }

        if (payload is StateChanged sc && sc.To.IsTerminal())
        {
            foreach (var key in _stageTrackers.Keys.Where(k => k.RunId == runId).ToList())
            {
                _stageTrackers.TryRemove(key, out _);
            }
        }

        return await AppendCoreAsync(runId, stageId, payload, cancellationToken).ConfigureAwait(false);
    }

    private static (string Text, int ByteCount) TruncateToUtf8Bytes(string text, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(text))
        {
            return (string.Empty, 0);
        }

        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return (text, Encoding.UTF8.GetByteCount(text));
        }

        var low = 0;
        var high = Math.Min(text.Length, maxBytes);
        var bestChars = 0;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var bytes = Encoding.UTF8.GetByteCount(text.AsSpan(0, mid));
            if (bytes <= maxBytes)
            {
                bestChars = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (bestChars > 0 && char.IsHighSurrogate(text[bestChars - 1]))
        {
            bestChars--;
        }

        return (text[..bestChars], Encoding.UTF8.GetByteCount(text.AsSpan(0, bestChars)));
    }

    private async Task<RunEvent> AppendCoreAsync(Guid runId, Guid? stageId, RunEvent payload, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (!await context.Runs.AnyAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"No existe la Ejecución {runId}.");
            }

            var appended = await log.AddAsync(context, runId, payload, cancellationToken, stageId).ConfigureAwait(false);
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return appended;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && RunEventLog.IsUniqueViolation(ex))
            {
                // Otro escritor tomó el mismo seq: se recalcula.
            }
        }
    }
}
