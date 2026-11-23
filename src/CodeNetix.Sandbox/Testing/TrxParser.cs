using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using CodeNetix.Contracts;

namespace CodeNetix.Sandbox.Testing;

/// <summary>
/// Parsea archivos TRX generados por <c>dotnet test --logger trx</c> usando únicamente <c>System.Xml.Linq</c>
/// (sin dependencias NuGet externas) y calcula los tests nuevos respecto a una <see cref="TestBaseline"/>.
/// </summary>
internal static class TrxParser
{
    /// <summary>Resultado intermedio de uno o varios archivos TRX ya parseados.</summary>
    internal sealed record ParsedTrx(
        IReadOnlyList<TestCaseResult> Results,
        int TotalTests,
        int PassedTests,
        int FailedTests,
        int SkippedTests,
        TimeSpan Duration);

    /// <summary>Extrae todos los casos de prueba, contadores y duración de un XML TRX.</summary>
    public static ParsedTrx Parse(string trxXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trxXml);

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var stringReader = new StringReader(trxXml);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(xmlReader);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"El archivo TRX no es un XML válido: {ex.Message}", ex);
        }

        var root = document.Root ?? throw new FormatException("El archivo TRX no tiene elemento raíz.");
        var ns = root.Name.Namespace;

        // Mapa testId -> className (para cualificar nombres cortos en MSTest si hace falta).
        var classByTestId = new Dictionary<string, string>(StringComparer.Ordinal);
        var definitions = root.Element(ns + "TestDefinitions");
        if (definitions is not null)
        {
            foreach (var unitTest in definitions.Elements(ns + "UnitTest"))
            {
                var id = unitTest.Attribute("id")?.Value;
                var className = unitTest.Element(ns + "TestMethod")?.Attribute("className")?.Value;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(className))
                {
                    var comma = className.IndexOf(',', StringComparison.Ordinal);
                    classByTestId[id] = comma >= 0 ? className[..comma].Trim() : className.Trim();
                }
            }
        }

        var results = new List<TestCaseResult>();
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var summedDuration = TimeSpan.Zero;

        var resultsElement = root.Element(ns + "Results");
        if (resultsElement is not null)
        {
            foreach (var element in resultsElement.Elements(ns + "UnitTestResult"))
            {
                var innerResults = element.Element(ns + "InnerResults")?.Elements(ns + "UnitTestResult").ToList();
                if (innerResults is { Count: > 0 })
                {
                    foreach (var inner in innerResults)
                    {
                        AddUnitTestResult(inner, ns, classByTestId, results, ref passed, ref failed, ref skipped, ref summedDuration);
                    }
                }
                else
                {
                    AddUnitTestResult(element, ns, classByTestId, results, ref passed, ref failed, ref skipped, ref summedDuration);
                }
            }
        }

        var duration = ParseTimesDuration(root.Element(ns + "Times")) ?? summedDuration;
        return new ParsedTrx(results, results.Count, passed, failed, skipped, duration);
    }

    /// <summary>Combina varios archivos TRX (soluciones con múltiples proyectos de test) en un único resultado.</summary>
    public static ParsedTrx Merge(IEnumerable<ParsedTrx> parsedRuns)
    {
        ArgumentNullException.ThrowIfNull(parsedRuns);

        var results = new List<TestCaseResult>();
        var total = 0;
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var duration = TimeSpan.Zero;

        foreach (var run in parsedRuns)
        {
            results.AddRange(run.Results);
            total += run.TotalTests;
            passed += run.PassedTests;
            failed += run.FailedTests;
            skipped += run.SkippedTests;
            duration += run.Duration;
        }

        return new ParsedTrx(results, total, passed, failed, skipped, duration);
    }

    /// <summary>Construye la <see cref="TestBaseline"/> a partir de los resultados de una ejecución base.</summary>
    public static TestBaseline ToBaseline(ParsedTrx parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var names = new HashSet<string>(parsed.Results.Select(r => r.Name), StringComparer.Ordinal);
        return new TestBaseline(names);
    }

    /// <summary>Construye un <see cref="TestReport"/> comparando <paramref name="parsed"/> con <paramref name="baseline"/>.</summary>
    public static TestReport ToReport(ParsedTrx parsed, TestBaseline baseline, TimeSpan? elapsedOverride = null)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(baseline);

        // Solo cuentan como tests nuevos los tests en verde que no existían en la línea base
        // (evita que un test omitido o con Skip satisfaga el requisito de test nuevo).
        var newTests = parsed.Results
            .Where(r => string.Equals(r.Outcome, "Passed", StringComparison.OrdinalIgnoreCase)
                && !baseline.ExistingTests.Contains(r.Name))
            .Select(r => r.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var failedCases = parsed.Results
            .Where(r => !string.Equals(r.Outcome, "Passed", StringComparison.OrdinalIgnoreCase)
                && !IsSkippedOutcome(r.Outcome))
            .ToArray();

        var duration = parsed.Duration > TimeSpan.Zero
            ? parsed.Duration
            : elapsedOverride ?? TimeSpan.Zero;

        return new TestReport(
            BuildSucceeded: true,
            TotalTests: parsed.TotalTests,
            PassedTests: parsed.PassedTests,
            FailedTests: parsed.FailedTests,
            SkippedTests: parsed.SkippedTests,
            NewTests: newTests,
            FailedTestCases: failedCases,
            Duration: duration);
    }

    private static void AddUnitTestResult(
        XElement element,
        XNamespace ns,
        Dictionary<string, string> classByTestId,
        List<TestCaseResult> results,
        ref int passed,
        ref int failed,
        ref int skipped,
        ref TimeSpan summedDuration)
    {
        var rawName = element.Attribute("testName")?.Value?.Trim();
        if (string.IsNullOrEmpty(rawName))
        {
            return;
        }

        var testId = element.Attribute("testId")?.Value;
        var fullName = ResolveFullName(rawName, testId, classByTestId);
        var outcome = element.Attribute("outcome")?.Value?.Trim() ?? "Unknown";

        var errorInfo = element.Element(ns + "Output")?.Element(ns + "ErrorInfo");
        var errorMessage = errorInfo?.Element(ns + "Message")?.Value?.Trim();
        var stackTrace = errorInfo?.Element(ns + "StackTrace")?.Value?.Trim();

        if (TimeSpan.TryParse(element.Attribute("duration")?.Value, CultureInfo.InvariantCulture, out var caseDuration))
        {
            summedDuration += caseDuration;
        }

        if (string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase))
        {
            passed++;
        }
        else if (IsSkippedOutcome(outcome))
        {
            skipped++;
        }
        else
        {
            failed++;
        }

        results.Add(new TestCaseResult(
            fullName,
            outcome,
            string.IsNullOrEmpty(errorMessage) ? null : errorMessage,
            string.IsNullOrEmpty(stackTrace) ? null : stackTrace));
    }

    private static string ResolveFullName(string rawName, string? testId, Dictionary<string, string> classByTestId)
    {
        var parenIndex = rawName.IndexOf('(', StringComparison.Ordinal);
        var nameBeforeParams = parenIndex >= 0 ? rawName[..parenIndex] : rawName;
        if (testId is not null
            && classByTestId.TryGetValue(testId, out var className)
            && !nameBeforeParams.Contains('.', StringComparison.Ordinal))
        {
            return $"{className}.{rawName}";
        }

        return rawName;
    }

    private static bool IsSkippedOutcome(string outcome) =>
        outcome.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase)
        || outcome.Equals("Skipped", StringComparison.OrdinalIgnoreCase)
        || outcome.Equals("Pending", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? ParseTimesDuration(XElement? times)
    {
        if (times is null)
        {
            return null;
        }

        var startRaw = times.Attribute("start")?.Value;
        var finishRaw = times.Attribute("finish")?.Value;
        if (DateTimeOffset.TryParse(startRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start)
            && DateTimeOffset.TryParse(finishRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var finish)
            && finish >= start)
        {
            return finish - start;
        }

        return null;
    }
}
