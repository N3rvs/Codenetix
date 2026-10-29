using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CodeNetix.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeNetix.Agents.Features.ImplementSpec;

/// <summary>
/// Programador (FR-11, AD-5): ejecuta Claude Code dentro del Sandbox de la Ejecución.
/// <list type="number">
/// <item>Asegura el CLI en <c>codenetix-tools</c> (<see cref="ISandboxToolsProvisioner"/>).</item>
/// <item>Ejecuta <c>claude --bare -p &lt;prompt&gt; --output-format json --permission-mode acceptEdits --allowedTools
/// "Bash,Read,Edit,Write" --permission-prompts none</c> con el modelo en <c>ANTHROPIC_MODEL</c>. <c>--bare</c> hace que
/// se ignoren los hooks y servidores MCP del repositorio clonado.</item>
/// <item>Publica <c>stdout</c> y <c>stderr</c> como <see cref="OutputAppended"/> (el sumidero los redacta y limita).</item>
/// <item>Timeout, código distinto de 0, <c>"is_error": true</c> o ningún cambio en <c>/workspace</c> →
/// <see cref="CodingResult.Failed"/>.</item>
/// </list>
/// </summary>
internal sealed partial class ClaudeCodeAgent(
    ISandboxToolsProvisioner tools,
    IRunEventSink events,
    IOptions<AgentsOptions> options,
    ILogger<ClaudeCodeAgent> logger) : ICodingAgent
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
        var command = BuildCommand(CoderPrompt.Build(input), model);

        SandboxExecResult result;
        try
        {
            result = await input.Sandbox.ExecAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException ex)
        {
            return new CodingResult.Failed($"el Sandbox no está disponible: {ex.Message}");
        }

        await PublishAsync(input.RunId, result.StdOut, "stdout", cancellationToken).ConfigureAwait(false);
        await PublishAsync(input.RunId, result.StdErr, "stderr", cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            return new CodingResult.Failed(
                $"el Programador superó el tiempo máximo de {Timeout.TotalMinutes.ToString(CultureInfo.InvariantCulture)} min");
        }

        var (isError, summary) = ParseOutput(result.StdOut);
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
    internal static SandboxCommand BuildCommand(string prompt, string model) =>
        new(
            Executable,
            [
                "--bare",
                "-p",
                prompt,
                "--output-format",
                "json",
                "--permission-mode",
                "acceptEdits",
                "--allowedTools",
                "Bash,Read,Edit,Write",
                "--permission-prompts",
                "none",
            ])
        {
            Environment = ImmutableDictionary<string, string>.Empty.Add("ANTHROPIC_MODEL", model),
            Timeout = Timeout,
        };

    /// <summary>
    /// <c>is_error</c> y <c>result</c> del JSON de <c>--output-format json</c>. Una salida que no es JSON no se
    /// considera error por sí misma (lo decide el código de salida).
    /// </summary>
    internal static (bool IsError, string? Summary) ParseOutput(string stdout)
    {
        var start = stdout.IndexOf('{', StringComparison.Ordinal);
        var end = stdout.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return (false, null);
        }

        try
        {
            using var json = JsonDocument.Parse(stdout.AsMemory(start, end - start + 1));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (false, null);
            }

            var isError = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
            var summary = (root.TryGetProperty("result", out var text) || root.TryGetProperty("error", out text) || root.TryGetProperty("message", out text)) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            return (isError, summary);
        }
        catch (JsonException)
        {
            return (false, null);
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

