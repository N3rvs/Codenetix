using CodeNetix.Host.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace CodeNetix.Host.Tests;

public sealed class SecretRedactorTests
{
    private const string GitHubToken = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";

    [Theory]
    [InlineData("sk-ant-api03-0000000000abcd", "sk-…abcd")]
    [InlineData("123456789012", "123…9012")] // exactamente 12
    [InlineData("12345678901", "…")] // 11
    [InlineData("abc", "…")]
    [InlineData("", "…")]
    [InlineData(GitHubToken, "ghp…wxyz")]
    public void Mask_table(string secret, string expected)
    {
        Assert.Equal(expected, SecretRedactor.Mask(secret));
    }

    [Fact]
    public void Known_secrets_are_read_from_every_secret_key_case_insensitively()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CODENETIX:GitHub:Token"] = "value-token-0001",
            ["CODENETIX:Providers:Local:apikey"] = "value-apikey-0002",
            ["X:AuthToken"] = "value-authtoken-0003",
            ["X:ACCESSTOKEN"] = "value-accesstoken-0004",
            ["X:Secret"] = "value-secret-0005",
            ["X:WebhookSecret"] = "value-webhook-0006",
            ["Password"] = "value-password-0007",
            ["CODENETIX:Providers:Local:BaseUrl"] = "http://localhost:11434",
        }).Build();
        var redactor = new SecretRedactor(configuration);

        var input = string.Join(' ', configuration.AsEnumerable().Select(p => p.Value).Where(v => v is not null));
        var output = redactor.Redact(input);

        for (var i = 1; i <= 7; i++)
        {
            Assert.DoesNotContain($"-000{i}", output, StringComparison.Ordinal);
        }

        Assert.Contains("http://localhost:11434", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_short_secret_is_fully_masked()
    {
        var redactor = new SecretRedactor(["hunter22"]);

        Assert.Equal("password=…", redactor.Redact("password=hunter22"));
    }

    [Fact]
    public void Json_escaped_secret_is_masked()
    {
        const string secret = "abc+def<ghi>jkl&mnop";
        var redactor = new SecretRedactor([secret]);
        var json = System.Text.Json.JsonSerializer.Serialize(new { key = secret });

        var output = redactor.Redact(json);

        Assert.DoesNotContain("def", output, StringComparison.Ordinal);
        Assert.Contains("abc…mnop", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Url_escaped_secret_is_masked()
    {
        const string secret = "key with/slash+plus=0123";
        var redactor = new SecretRedactor([secret]);

        var output = redactor.Redact($"GET /api/x?k={Uri.EscapeDataString(secret)}");

        Assert.Equal("GET /api/x?k=key…0123", output);
    }

    [Theory]
    [InlineData("sk-ant-api03-" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "sk-…AAAA")]
    [InlineData("sk-proj-0123456789abcdefghij", "sk-…ghij")]
    [InlineData("ghp_0123456789abcdefghijWXYZ", "ghp…WXYZ")]
    [InlineData("gho_0123456789abcdefghijWXYZ", "gho…WXYZ")]
    [InlineData("ghu_0123456789abcdefghijWXYZ", "ghu…WXYZ")]
    [InlineData("ghs_0123456789abcdefghijWXYZ", "ghs…WXYZ")]
    [InlineData("ghr_0123456789abcdefghijWXYZ", "ghr…WXYZ")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefWXYZ", "git…WXYZ")]
    public void Unconfigured_key_patterns_are_masked(string key, string expectedMask)
    {
        var redactor = new SecretRedactor([]);

        var output = redactor.Redact($"usando \"{key}\" ahora");

        Assert.Equal($"usando \"{expectedMask}\" ahora", output);
    }

    [Theory]
    [InlineData("instalado sk-learn y scikit")]
    [InlineData("ghp_short")]
    [InlineData("risk-assessment-0123456789abcdefgh")]
    public void Ordinary_text_is_left_alone(string text)
    {
        Assert.Equal(text, new SecretRedactor([]).Redact(text));
    }

    [Fact]
    public void Console_formatter_masks_configured_token_in_message_and_exception()
    {
        var redactor = new SecretRedactor([GitHubToken]);
        var formatter = new RedactingConsoleFormatter(redactor, new StaticOptionsMonitor(new ConsoleFormatterOptions()));
        var exception = new InvalidOperationException($"fallo con {GitHubToken}");
        var entry = new LogEntry<string>(
            LogLevel.Warning,
            "CodeNetix.Test",
            new EventId(7),
            $"token={GitHubToken}",
            exception,
            (state, _) => state);
        using var writer = new StringWriter();

        formatter.Write(entry, scopeProvider: null, writer);

        var output = writer.ToString();
        Assert.DoesNotContain(GitHubToken, output, StringComparison.Ordinal);
        Assert.Contains("token=ghp…wxyz", output, StringComparison.Ordinal);
        Assert.Contains("fallo con ghp…wxyz", output, StringComparison.Ordinal);
        Assert.StartsWith("warn: CodeNetix.Test[7]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Console_formatter_masks_unconfigured_pattern()
    {
        var key = "sk-ant-" + new string('x', 36) + "1234";
        var formatter = new RedactingConsoleFormatter(new SecretRedactor([]), new StaticOptionsMonitor(new ConsoleFormatterOptions()));
        var entry = new LogEntry<string>(LogLevel.Information, "c", new EventId(1), $"key {key}", null, (s, _) => s);
        using var writer = new StringWriter();

        formatter.Write(entry, null, writer);

        Assert.DoesNotContain(key, writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("key sk-…1234", writer.ToString(), StringComparison.Ordinal);
    }

    private sealed class StaticOptionsMonitor(ConsoleFormatterOptions value) : IOptionsMonitor<ConsoleFormatterOptions>
    {
        public ConsoleFormatterOptions CurrentValue => value;

        public ConsoleFormatterOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ConsoleFormatterOptions, string?> listener) => null;
    }
}
