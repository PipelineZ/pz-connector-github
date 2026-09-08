using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github.Tests;

public sealed class GithubHttpClientFactoryTests
{
    private static GithubConnectionConfig ParseOrThrow(Dictionary<string, object?> values)
    {
        var errors = new List<string>();
        return GithubConnectionConfig.Parse(new ConnectorConfig(values), errors)
            ?? throw new InvalidOperationException(string.Join("; ", errors));
    }

    [Fact]
    public void BaseAddress_is_the_configured_url_normalized_to_a_trailing_slash()
    {
        var config = ParseOrThrow(new() { ["url"] = "https://example.com/api" });

        using var client = GithubHttpClientFactory.Create(config);

        Assert.Equal(new Uri("https://example.com/api/"), client.BaseAddress);
    }

    [Fact]
    public void BaseAddress_with_a_path_segment_is_normalized_so_a_relative_request_appends_rather_than_replaces_it()
    {
        // Per RFC 3986 §5.3, combining a base URI with an absolute-path reference (a leading '/')
        // REPLACES the base's path entirely: "https://ghe.example.com/api/v3" + "/repos/o/r/issues"
        // -> ".../repos/o/r/issues", silently dropping "/api/v3" -- a documented GHES deployment mode
        // that would be broken outright without this normalization. A trailing-slash base combined
        // with a RELATIVE reference (this connector's request paths carry no leading '/') appends
        // instead: this is the mechanism every request path in this connector relies on.
        var config = ParseOrThrow(new() { ["url"] = "https://ghe.example.com/api/v3" });

        using var client = GithubHttpClientFactory.Create(config);
        var requestUri = new Uri(client.BaseAddress!, "repos/o/r/issues");

        Assert.Equal("/api/v3/repos/o/r/issues", requestUri.PathAndQuery);
    }

    [Fact]
    public void Default_headers_are_set()
    {
        var config = ParseOrThrow([]);

        using var client = GithubHttpClientFactory.Create(config);

        Assert.Equal("application/vnd.github+json", string.Join(",", client.DefaultRequestHeaders.Accept));
        Assert.Equal("2022-11-28", client.DefaultRequestHeaders.GetValues("X-GitHub-Api-Version").Single());
        Assert.StartsWith("pz-connector-github/", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public void No_authorization_header_when_token_absent()
    {
        var config = ParseOrThrow([]);

        using var client = GithubHttpClientFactory.Create(config);

        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void Authorization_header_set_when_token_present()
    {
        var config = ParseOrThrow(new() { ["token"] = "ghp_abc123" });

        using var client = GithubHttpClientFactory.Create(config);

        Assert.NotNull(client.DefaultRequestHeaders.Authorization);
        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal("ghp_abc123", client.DefaultRequestHeaders.Authorization!.Parameter);
    }
}
