using Pz.Connectors.Abstractions;
using Pz.Connectors.TestKit;

namespace Pz.Connector.Github.Tests;

public sealed class GithubConnectorTests
{
    private static ConnectorConfig Config(Dictionary<string, object?>? values = null) => new(values ?? []);

    [Fact]
    public void Info_name_is_github()
    {
        var connector = new GithubConnector();
        Assert.Equal("github", connector.Info.Name);
    }

    [Fact]
    public void Info_protocol_major_matches_abi()
    {
        var connector = new GithubConnector();
        Assert.Equal(ProtocolVersion.Major, connector.Info.ProtocolMajor);
    }

    [Fact]
    public void Capabilities_are_none()
    {
        var connector = new GithubConnector();
        Assert.Equal(ConnectorCapabilities.None, connector.Capabilities);
    }

    [Fact]
    public async Task ValidateAsync_surfaces_config_parse_errors()
    {
        var connector = new GithubConnector();
        var result = await connector.ValidateAsync(Config(new() { ["url"] = "ftp://example.com" }), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("'url' must be an absolute http/https URL", result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_succeeds_for_empty_config()
    {
        var connector = new GithubConnector();
        var result = await connector.ValidateAsync(Config(), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task CheckConnectionAsync_returns_failed_check_for_invalid_config_without_throwing()
    {
        var connector = new GithubConnector();
        var check = await connector.CheckConnectionAsync(Config(new() { ["url"] = "ftp://example.com" }), CancellationToken.None);

        Assert.False(check.Ok);
        Assert.Contains("'url' must be an absolute http/https URL", check.Message);
    }

    [Fact]
    public async Task CheckConnectionAsync_unauthenticated_skips_user_call_and_reports_remaining()
    {
        await using var server = new StubHttpServer();
        server.Map("/rate_limit", _ => new StubResponse(200,
            """{"rate":{"limit":60,"remaining":59,"reset":1700000000}}"""));

        var connector = new GithubConnector();
        var check = await connector.CheckConnectionAsync(
            Config(new() { ["url"] = server.BaseUrl.ToString() }), CancellationToken.None);

        Assert.True(check.Ok);
        Assert.Equal("unauthenticated, 59/60 requests remaining", check.Message);
        Assert.Single(server.Requests);
        Assert.Equal("/rate_limit", server.Requests[0].Url.AbsolutePath);
    }

    [Fact]
    public async Task CheckConnectionAsync_authenticated_calls_user_and_reports_login()
    {
        await using var server = new StubHttpServer();
        server.Map("/rate_limit", _ => new StubResponse(200,
            """{"rate":{"limit":5000,"remaining":4999,"reset":1700000000}}"""));
        server.Map("/user", _ => new StubResponse(200, """{"login":"octocat"}"""));

        var connector = new GithubConnector();
        var check = await connector.CheckConnectionAsync(
            Config(new() { ["url"] = server.BaseUrl.ToString(), ["token"] = "ghp_faketoken123" }),
            CancellationToken.None);

        Assert.True(check.Ok);
        Assert.Equal("authenticated as octocat, 4999/5000 requests remaining", check.Message);
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains(server.Requests, r => r.Url.AbsolutePath == "/rate_limit");
        Assert.Contains(server.Requests, r => r.Url.AbsolutePath == "/user");
    }

    [Fact]
    public async Task CheckConnectionAsync_authenticated_request_carries_bearer_token()
    {
        await using var server = new StubHttpServer();
        string? authHeader = null;
        server.Map("/rate_limit", req =>
        {
            authHeader = req.Headers.TryGetValue("Authorization", out var v) ? v : null;
            return new StubResponse(200, """{"rate":{"limit":5000,"remaining":4999,"reset":1700000000}}""");
        });
        server.Map("/user", _ => new StubResponse(200, """{"login":"octocat"}"""));

        var connector = new GithubConnector();
        await connector.CheckConnectionAsync(
            Config(new() { ["url"] = server.BaseUrl.ToString(), ["token"] = "ghp_faketoken123" }),
            CancellationToken.None);

        Assert.Equal("Bearer ghp_faketoken123", authHeader);
    }

    [Fact]
    public async Task CheckConnectionAsync_non_2xx_rate_limit_response_fails_without_throwing()
    {
        await using var server = new StubHttpServer();
        server.Map("/rate_limit", _ => new StubResponse(401, """{"message":"Bad credentials"}"""));

        var connector = new GithubConnector();
        var check = await connector.CheckConnectionAsync(
            Config(new() { ["url"] = server.BaseUrl.ToString(), ["token"] = "ghp_faketoken123" }),
            CancellationToken.None);

        Assert.False(check.Ok);
        Assert.NotNull(check.Message);
        Assert.DoesNotContain("ghp_faketoken123", check.Message);
    }

    [Fact]
    public async Task CheckConnectionAsync_non_2xx_user_response_fails_without_throwing()
    {
        await using var server = new StubHttpServer();
        server.Map("/rate_limit", _ => new StubResponse(200,
            """{"rate":{"limit":5000,"remaining":4999,"reset":1700000000}}"""));
        server.Map("/user", _ => new StubResponse(403, """{"message":"Forbidden"}"""));

        var connector = new GithubConnector();
        var check = await connector.CheckConnectionAsync(
            Config(new() { ["url"] = server.BaseUrl.ToString(), ["token"] = "ghp_faketoken123" }),
            CancellationToken.None);

        Assert.False(check.Ok);
        Assert.NotNull(check.Message);
        Assert.DoesNotContain("ghp_faketoken123", check.Message);
    }

    [Fact]
    public async Task CheckConnectionAsync_connection_refused_fails_without_throwing()
    {
        var connector = new GithubConnector();
        var check = await connector.CheckConnectionAsync(
            Config(new() { ["url"] = "http://127.0.0.1:1" }), CancellationToken.None);

        Assert.False(check.Ok);
        Assert.NotNull(check.Message);
    }

    [Fact]
    public async Task CheckConnectionAsync_honors_cancellation()
    {
        await using var server = new StubHttpServer();
        var connector = new GithubConnector();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connector.CheckConnectionAsync(
                Config(new() { ["url"] = server.BaseUrl.ToString() }), cts.Token).AsTask());
    }

    [Fact]
    public async Task OpenAsync_returns_a_source_for_a_valid_connection()
    {
        ISourceConnector connector = new GithubConnector();
        var source = await connector.OpenAsync(Config(), CancellationToken.None);

        Assert.NotNull(source);
        await source.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_throws_a_non_transient_exception_for_an_invalid_connection()
    {
        ISourceConnector connector = new GithubConnector();

        var ex = await Assert.ThrowsAsync<PzConnectorException>(() =>
            connector.OpenAsync(Config(new() { ["url"] = "ftp://example.com" }), CancellationToken.None).AsTask());

        Assert.False(ex.IsTransient);
    }
}
