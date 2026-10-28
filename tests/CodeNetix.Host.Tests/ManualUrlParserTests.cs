using CodeNetix.Ingestion.Features.SubmitManualEvent;

namespace CodeNetix.Host.Tests;

/// <summary>Tabla de URLs de <c>codenetix fix</c> (historia 1.4).</summary>
public sealed class ManualUrlParserTests
{
    [Theory]
    [InlineData("https://github.com/O/R/issues/12", "https://github.com/o/r/issues/12", "github:o/r#12")]
    [InlineData("https://www.github.com/O/R/issues/12", "https://github.com/o/r/issues/12", "github:o/r#12")]
    [InlineData("https://github.com/O/R/issues/12/", "https://github.com/o/r/issues/12", "github:o/r#12")]
    [InlineData("https://github.com/O/R/issues/12?x=1", "https://github.com/o/r/issues/12", "github:o/r#12")]
    [InlineData("https://github.com/O/R/issues/12#c", "https://github.com/o/r/issues/12", "github:o/r#12")]
    [InlineData("https://www.github.com/O/R/issues/12/?x=1#issuecomment-9", "https://github.com/o/r/issues/12", "github:o/r#12")]
    [InlineData("  https://GitHub.com/Acme-Corp/My.Repo_x/issues/007  ", "https://github.com/acme-corp/my.repo_x/issues/7", "github:acme-corp/my.repo_x#7")]
    [InlineData("https://github.com/o/r.git/issues/1", "https://github.com/o/r/issues/1", "github:o/r#1")]
    [InlineData("https://github.com/O/R.GIT/issues/1", "https://github.com/o/r/issues/1", "github:o/r#1")]
    public void GitHub_issue_urls_are_canonicalized(string input, string canonicalUrl, string objectKey)
    {
        var result = ManualUrlParser.Parse(input);

        Assert.Equal(ManualUrlKind.GitHubIssue, result.Kind);
        Assert.Equal(canonicalUrl, result.CanonicalUrl);
        Assert.Equal(objectKey, result.ObjectKey);
    }

    [Theory]
    [InlineData("https://acme.sentry.io/issues/123/")]
    [InlineData("https://sentry.io/organizations/acme/issues/123/")]
    [InlineData("https://sentry.example.com/organizations/acme/issues/42/?project=1")]
    [InlineData("http://sentry.internal:9000/organizations/acme/issues/42")]
    public void Sentry_urls_are_recognized(string input)
    {
        var result = ManualUrlParser.Parse(input);

        Assert.Equal(ManualUrlKind.Sentry, result.Kind);
        Assert.Null(result.CanonicalUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("foo")]
    [InlineData("github.com/o/r/issues/1")]
    [InlineData("https://github.com/o/r/pull/3")]
    [InlineData("https://github.com/o/r/commit/0123abc")]
    [InlineData("https://github.com/o/r/issues")]
    [InlineData("https://github.com/o/r/issues/0")]
    [InlineData("https://github.com/o/r/issues/abc")]
    [InlineData("https://github.com/o/r/issues/-1")]
    [InlineData("https://github.com/o/r/issues/1/comments")]
    [InlineData("https://github.com/o/r/Issues/1")]
    [InlineData("https://github.com/o//r/issues/1")]
    [InlineData("https://github.com/-o/r/issues/1")]
    [InlineData("https://github.com/o/../issues/1")]
    [InlineData("https://github.com/o/.git/issues/1")]
    [InlineData("http://github.com/o/r/issues/1")]
    [InlineData("https://github.com:8443/o/r/issues/1")]
    [InlineData("https://user:pass@github.com/o/r/issues/1")]
    [InlineData("https://gitlab.com/o/r/-/issues/1")]
    [InlineData("https://gist.github.com/o/r/issues/1")]
    [InlineData("https://github.com.evil.example/o/r/issues/1")]
    [InlineData("https://evilsentry.io/issues/1")]
    [InlineData("ftp://github.com/o/r/issues/1")]
    [InlineData("file:///etc/passwd")]
    public void Anything_else_is_unsupported(string? input)
    {
        var result = ManualUrlParser.Parse(input);

        Assert.Equal(ManualUrlKind.Unsupported, result.Kind);
        Assert.Null(result.CanonicalUrl);
        Assert.Null(result.ObjectKey);
    }
}
