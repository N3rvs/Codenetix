using System.Text;
using CodeNetix.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Secrets;

/// <summary>
/// Formateador de consola (sumidero de logs) que pasa cada entrada completa, mensaje,
/// ámbitos y excepción, por <see cref="ISecretRedactor"/> antes de escribirla.
/// Formato equivalente al "simple" sin colores: <c>info: Categoría[Id]</c> y el mensaje sangrado.
/// </summary>
internal sealed class RedactingConsoleFormatter(ISecretRedactor redactor, IOptionsMonitor<ConsoleFormatterOptions> options)
    : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "codenetix-redacting";

    private const string Padding = "      ";

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        var current = options.CurrentValue;
        var builder = new StringBuilder();

        if (current.TimestampFormat is { } timestampFormat)
        {
            var now = current.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
            builder.Append(now.ToString(timestampFormat, System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.Append(LevelText(logEntry.LogLevel))
            .Append(": ")
            .Append(logEntry.Category)
            .Append('[')
            .Append(logEntry.EventId.Id)
            .Append(']')
            .AppendLine();

        if (current.IncludeScopes && scopeProvider is not null)
        {
            var scopes = new StringBuilder();
            scopeProvider.ForEachScope((scope, sb) => sb.Append(" => ").Append(scope), scopes);
            if (scopes.Length > 0)
            {
                builder.Append(Padding).Append(scopes).AppendLine();
            }
        }

        if (!string.IsNullOrEmpty(message))
        {
            builder.Append(Padding)
                .Append(message.ReplaceLineEndings(Environment.NewLine + Padding))
                .AppendLine();
        }

        if (logEntry.Exception is not null)
        {
            builder.Append(Padding)
                .Append(logEntry.Exception.ToString().ReplaceLineEndings(Environment.NewLine + Padding))
                .AppendLine();
        }

        textWriter.Write(redactor.Redact(builder.ToString()));
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => level.ToString().ToLowerInvariant(),
    };
}
