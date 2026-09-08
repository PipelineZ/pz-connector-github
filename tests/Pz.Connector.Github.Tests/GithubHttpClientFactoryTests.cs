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
    public void BaseAddress_is_the_configured_url()
    {
        var config = ParseOrThrow(new() { ["url"] = "https://example.com/api" });

        using var client = GithubHttpClientFactory.Create(config);

        Assert.Equal(new Uri("https://example.com/api"), client.BaseAddress);
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
