using System.Text.Json;
using CodeNetix.Contracts;

namespace CodeNetix.Pipeline.Domain;

/// <summary>Fila de <c>run_test_reports</c>: último <see cref="TestReport"/> de una Ejecución.</summary>
internal sealed class RunTestReportRecord
{
    public Guid RunId { get; set; }

    /// <summary>JSON camelCase del <see cref="TestReport"/>.</summary>
    public string ReportJson { get; set; } = string.Empty;

    public bool Passed { get; set; }

    public int TotalTests { get; set; }

    public int FailedTests { get; set; }

    public int NewTestsCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>JSON de un <see cref="TestReport"/> en <c>run_test_reports.report_json</c> (camelCase).</summary>
internal static class TestReportSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(TestReport report) => JsonSerializer.Serialize(report, Options);

    public static TestReport Deserialize(string json) =>
        JsonSerializer.Deserialize<TestReport>(json, Options)
        ?? throw new JsonException("TestReport nulo.");
}
