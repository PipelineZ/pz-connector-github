using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github.Tests;

public sealed class GithubConnectionConfigTests
{
    private static ConnectorConfig Config(Dictionary<string, object?> values) => new(values);

    [Fact]
    public void Empty_config_defaults_url_and_leaves_token_null()
    {
        var errors = new List<string>();
        var config = GithubConnectionConfig.Parse(Config([]), errors);

        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Equal(new Uri("https://api.github.com"), config.Url);
        Assert.Null(config.Token);
    }

    [Fact]
    public void Url_with_non_http_scheme_errors()
    {
        var errors = new List<string>();
        var config = GithubConnectionConfig.Parse(Config(new() { ["url"] = "ftp://example.com" }), errors);

        Assert.Null(config);
        Assert.Contains("'url' must be an absolute http/https URL", errors);
    }

    [Fact]
    public void Url_that_is_not_absolute_errors()
    {
        var errors = new List<string>();
        var config = GithubConnectionConfig.Parse(Config(new() { ["url"] = "not-a-url" }), errors);

        Assert.Null(config);
        Assert.Contains("'url' must be an absolute http/https URL", errors);
    }

    [Fact]
    public void Url_trailing_slash_is_trimmed()
    {
        var errors = new List<string>();
        var config = GithubConnectionConfig.Parse(Config(new() { ["url"] = "https://example.com/" }), errors);

        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Equal(new Uri("https://example.com"), config.Url);
        Assert.Equal("https://example.com/", config.Url.ToString());
    }

    [Fact]
    public void Url_without_trailing_slash_is_unchanged()
    {
        var errors = new List<string>();
        var config = GithubConnectionConfig.Parse(Config(new() { ["url"] = "https://example.com/api" }), errors);

        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Equal(new Uri("https://example.com/api"), config.Url);
    }

    [Fact]
    public void Token_empty_string_errors()
    {
        var errors = new List<string>();
        var config = GithubConnectionConfig.Parse(Config(new() { ["token"] = "" }), errors);

        Assert.Null(config);
        Assert.Contains("'token' must not be empty", errors);
    }

    [Fact]
    public void Token_whitespace_only_errors()
    {
        var errors = new List<string>();
        var config = GithubConnectionConfig.Parse(Config(new() { ["token"] = "   " }), errors);

        Assert.Null(config);
        Assert.Contains("'token' must not be empty", errors);
    }

    [Fact]
    public void Token_absent_leaves_token_null_and_no_authorization()
    {
        var errors = new List<string>();
        var config = GithubConnectionConfig.Parse(Config([]), errors);

        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Null(config.Token);
    }

    [Fact]
    public void Token_present_registers_with_redactor()
    {
        var errors = new List<string>();
        var config = GithubConnectionConfig.Parse(Config(new() { ["token"] = "ghp_s3cret_value" }), errors);

        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Equal("ghp_s3cret_value", config.Token);
        Assert.Equal("secret is ***", config.Redactor.Redact("secret is ghp_s3cret_value"));
    }

    [Fact]
    public void Unknown_key_errors_naming_known_keys()
    {
        var errors = new List<string>();
        var config = GithubConnectionConfig.Parse(Config(new() { ["bogus"] = "x" }), errors);

        Assert.Null(config);
        Assert.Contains("unknown connection key 'bogus'; known keys: url, token", errors);
    }

    [Fact]
    public void Errors_aggregate_bad_url_and_empty_token()
    {
        var errors = new List<string>();
        var config = GithubConnectionConfig.Parse(
            Config(new() { ["url"] = "ftp://example.com", ["token"] = "" }), errors);

        Assert.Null(config);
        Assert.Equal(2, errors.Count);
        Assert.Contains("'url' must be an absolute http/https URL", errors);
        Assert.Contains("'token' must not be empty", errors);
    }
}
