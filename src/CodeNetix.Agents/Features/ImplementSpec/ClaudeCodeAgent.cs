using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CodeNetix.Agents.ChatClients;
using CodeNetix.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Agents.Features.ImplementSpec;

/// <summary>
/// Programador (FR-11, FR-13, AD-5, AD-15): ejecuta Claude Code dentro del Sandbox de la Ejecución.
/// <list type="number">
/// <item>Comprueba el presupuesto restante mediante <see cref="IBudgetLedger"/> y añade
/// <c>--max-budget-usd &lt;restante&gt;</c> si <c>EffectiveConfig.BudgetUsd</c> está configurado.</item>
/// <item>Asegura el CLI en <c>codenetix-tools</c> (<see cref="ISandboxToolsProvisioner"/>).</item>
/// <item>Ejecuta <c>claude --bare -p &lt;prompt&gt; --output-format json --permission-mode acceptEdits --allowedTools
/// "Bash,Read,Edit,Write" --permission-prompts none</c> (más <c>--max-budget-usd</c> si aplica) con el modelo en
/// <c>ANTHROPIC_MODEL</c>. <c>--bare</c> hace que se ignoren los hooks y servidores MCP del repositorio clonado.</item>
/// <item>Publica <c>stdout</c> y <c>stderr</c> como <see cref="OutputAppended"/> (el sumidero los redacta y limita).</item>
/// <item>Si el presupuesto se agotó (<see cref="BudgetCodes.BudgetExhausted"/> en salida o en el libro de presupuesto),
/// devuelve <see cref="CodingResult.BudgetExhausted"/>. Timeout, código distinto de 0, <c>"is_error": true</c> o
/// ningún cambio en <c>/workspace</c> → <see cref="CodingResult.Failed"/>.</item>
/// </list>
/// </summary>
internal sealed partial class ClaudeCodeAgent(
    ISandboxToolsProvisioner tools,
    IRunEventSink events,
    IOptions<AgentsOptions> options,
    ILogger<ClaudeCodeAgent> logger,
    IBudgetLedger? budgetLedger = null) : ICodingAgent
{
    public const string Executable = "claude";

    public const string DefaultModel = "claude-sonnet-5-5";

    public const string NoChangesReason = "el Programador terminó sin cambios en el árbol de trabajo";

    /// <summary>Tiempo máximo de un intento del CLI.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    /// <summary>Longitud máxima del extracto de salida que acompaña a un motivo de fallo.</summary>
    private const int MaxExcerptLength = 400;

    public async Task<CodingResult> RunAsync(CodingInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        decimal? remainingBudgetUsd = null;
        if (input.Config.BudgetUsd is { } limit)
        {
            var currentCost = budgetLedger is not null
                ? await budgetLedger.GetRunCostAsync(input.RunId, cancellationToken).ConfigureAwait(false)
                : null;

            if (currentCost is { IsExhausted: true })
            {
                return new CodingResult.BudgetExhausted(
                    BudgetTrackingChatClient.FormatExhaustedDetail(currentCost.EstimatedCostUsd, currentCost.BudgetLimitUsd));
            }

            var remaining = currentCost is { IsEstimable: true, EstimatedCostUsd: { } spent }
                ? Math.Max(0m, decimal.Round(limit - spent, 8, MidpointRounding.AwayFromZero))
                : limit;

            if (remaining <= 0m)
            {
                return new CodingResult.BudgetExhausted(
                    BudgetTrackingChatClient.FormatExhaustedDetail(currentCost?.EstimatedCostUsd ?? limit, limit));
            }

            remainingBudgetUsd = remaining;
        }

        var model = ResolveModel(input.Config, options.Value);
        var shortId = RunShortId.Of(input.RunId);
        try
        {
            await tools.EnsureClaudeCliAsync(input.Sandbox, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException ex)
        {
            return new CodingResult.Failed($"no se pudo preparar el CLI del Programador: {ex.Message}");
        }

        LogCoding(shortId, input.Attempt, model);
        var command = BuildCommand(CoderPrompt.Build(input), model, remainingBudgetUsd);

        SandboxExecResult result;
        try
        {
            result = await input.Sandbox.ExecAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException ex)
        {
            return new CodingResult.Failed($"el Sandbox no está disponible: {ex.Message}");
        }

        await PublishAsync(input.RunId, result.StdOut, "stdout", CancellationToken.None).ConfigureAwait(false);
        await PublishAsync(input.RunId, result.StdErr, "stderr", CancellationToken.None).ConfigureAwait(false);

        RunCostSummary? postCost = null;
        if (budgetLedger is not null)
        {
            postCost = await budgetLedger.GetRunCostAsync(input.RunId, CancellationToken.None).ConfigureAwait(false);
            if (postCost.IsExhausted)
            {
                return new CodingResult.BudgetExhausted(
                    BudgetTrackingChatClient.FormatExhaustedDetail(postCost.EstimatedCostUsd, postCost.BudgetLimitUsd));
            }
        }

        var (isError, summary, isMaxBudgetError) = ParseOutput(result.StdOut);
        var cliFailed = result.ExitCode != 0 || isError || result.TimedOut;
        var hasBudgetLimitOrNoLedger = budgetLedger is null || input.Config.BudgetUsd.HasValue;
        if (cliFailed
            && hasBudgetLimitOrNoLedger
            && (isMaxBudgetError || ContainsBudgetExhaustedCode(result.StdOut) || ContainsBudgetExhaustedCode(result.StdErr)))
        {
            var detail = postCost is { IsExhausted: true }
                ? BudgetTrackingChatClient.FormatExhaustedDetail(postCost.EstimatedCostUsd, postCost.BudgetLimitUsd)
                : isMaxBudgetError
                    ? "presupuesto agotado por el CLI del Programador (error_max_budget_usd)"
                    : $"presupuesto agotado ({BudgetCodes.BudgetExhausted})";
            return new CodingResult.BudgetExhausted(detail);
        }

        if (result.TimedOut)
        {
            return new CodingResult.Failed(
                $"el Programador superó el tiempo máximo de {Timeout.TotalMinutes.ToString(CultureInfo.InvariantCulture)} min");
        }

        if (result.ExitCode != 0)
        {
            var excerpt = Excerpt(summary ?? (string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr));
            return new CodingResult.Failed(
                $"el CLI del Programador terminó con código {result.ExitCode.ToString(CultureInfo.InvariantCulture)}{(excerpt.Length > 0 ? ": " + excerpt : string.Empty)}");
        }

        if (isError)
        {
            var excerpt = Excerpt(summary ?? string.Empty);
            return new CodingResult.Failed($"el CLI del Programador informó de un error{(excerpt.Length > 0 ? ": " + excerpt : string.Empty)}");
        }

        bool changed;
        try
        {
            changed = await input.Sandbox.HasChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException ex)
        {
            return new CodingResult.Failed($"no se pudo comprobar el árbol de trabajo: {ex.Message}");
        }

        return changed
            ? new CodingResult.Completed(Excerpt(summary ?? string.Empty), model)
            : new CodingResult.Failed(NoChangesReason);
    }

    /// <summary><c>models.coder</c> de la <see cref="EffectiveConfig"/>, el de la instancia o <see cref="DefaultModel"/>.</summary>
    internal static string ResolveModel(EffectiveConfig config, AgentsOptions agents) =>
        config.Models?.Coder is { } repoModel && !string.IsNullOrWhiteSpace(repoModel)
            ? repoModel.Trim()
            : agents.Coder?.Model is { } instanceModel && !string.IsNullOrWhiteSpace(instanceModel)
                ? instanceModel.Trim()
                : DefaultModel;

    /// <summary>El comando exacto de AD-5 (sin shell: el prompt viaja como un único argumento).</summary>
    internal static SandboxCommand BuildCommand(string prompt, string model, decimal? maxBudgetUsd = null)
    {
        var args = ImmutableArray.CreateBuilder<string>(maxBudgetUsd.HasValue ? 14 : 12);
        args.Add("--bare");
        args.Add("-p");
        args.Add(prompt);
        args.Add("--output-format");
        args.Add("json");
        args.Add("--permission-mode");
        args.Add("acceptEdits");
        args.Add("--allowedTools");
        args.Add("Bash,Read,Edit,Write");
        args.Add("--permission-prompts");
        args.Add("none");
        if (maxBudgetUsd.HasValue)
        {
            args.Add("--max-budget-usd");
            args.Add(maxBudgetUsd.Value.ToString("0.########", CultureInfo.InvariantCulture));
        }

        return new SandboxCommand(Executable, args.ToImmutable())
        {
            Environment = ImmutableDictionary<string, string>.Empty.Add("ANTHROPIC_MODEL", model),
            Timeout = Timeout,
        };
    }

    private static bool ContainsBudgetExhaustedCode(string text) =>
        !string.IsNullOrEmpty(text) && text.Contains(BudgetCodes.BudgetExhausted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>is_error</c>, <c>result</c> y <c>subtype == "error_max_budget_usd"</c> del JSON de <c>--output-format json</c>.
    /// Una salida que no es JSON no se considera error por sí misma (lo decide el código de salida).
    /// </summary>
    internal static (bool IsError, string? Summary, bool IsMaxBudgetError) ParseOutput(string stdout)
    {
        var start = stdout.IndexOf('{', StringComparison.Ordinal);
        var end = stdout.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return (false, null, false);
        }

        try
        {
            using var json = JsonDocument.Parse(stdout.AsMemory(start, end - start + 1));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (false, null, false);
            }

            var isError = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
            var isMaxBudgetError = root.TryGetProperty("subtype", out var subtype)
                && subtype.ValueKind == JsonValueKind.String
                && string.Equals(subtype.GetString(), "error_max_budget_usd", StringComparison.OrdinalIgnoreCase);
            var summary = (root.TryGetProperty("result", out var text) || root.TryGetProperty("error", out text) || root.TryGetProperty("message", out text)) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            return (isError, summary, isMaxBudgetError);
        }
        catch (JsonException)
        {
            return (false, null, false);
        }
    }

    private async Task PublishAsync(Guid runId, string text, string stream, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(text))
        {
            await events.AppendAsync(runId, null, new OutputAppended(text, stream), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Una línea y como mucho <see cref="MaxExcerptLength"/> caracteres.</summary>
    private static string Excerpt(string text)
    {
        var oneLine = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return TextLimits.Truncate(oneLine, MaxExcerptLength);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ejecución {ShortId}: intento {Attempt} del Programador con el modelo {Model}.")]
    private partial void LogCoding(string shortId, int attempt, string model);
}

