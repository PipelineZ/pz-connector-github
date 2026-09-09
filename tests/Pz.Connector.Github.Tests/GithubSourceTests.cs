using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github.Tests;

public sealed class GithubSourceTests
{
    private static HttpClient Client(FakeHandler handler) => new(handler) { BaseAddress = new Uri("https://api.github.com") };

    private static GithubConnectionConfig Connection() =>
        GithubConnectionConfig.Parse(new ConnectorConfig(new Dictionary<string, object?>()), [])!;

    private static GithubSource Source(FakeHandler handler) =>
        new(Connection(), Client(handler), NullLogger.Instance);

    private static DatasetSpec Spec(string dataset) => new("github", dataset, new Dictionary<string, object?>());

    [Theory]
    [InlineData("acme/widgets/issues")]
    [InlineData("acme/widgets/pulls")]
    [InlineData("acme/widgets/issues/comments")]
    [InlineData("acme/widgets/commits")]
    [InlineData("acme/widgets/releases")]
    [InlineData("acme/widgets/actions/runs")]
    public async Task GetSchemaAsync_resolves_the_dataset_and_returns_a_schema(string dataset)
    {
        var source = Source(new FakeHandler());

        var schema = await source.GetSchemaAsync(Spec(dataset), CancellationToken.None);

        Assert.True(schema.Schema.FieldsList.Count > 0);
    }

    [Fact]
    public void TryGetNativeScan_always_returns_false()
    {
        var source = Source(new FakeHandler());

        var result = source.TryGetNativeScan(Spec("acme/widgets/issues"), out var scan);

        Assert.False(result);
        Assert.Null(scan);
    }

    [Fact]
    public async Task PlanReadAsync_returns_one_partition()
    {
        var source = Source(new FakeHandler());

        var partitions = await source.PlanReadAsync(Spec("acme/widgets/issues"), ReadHints.None, CancellationToken.None);

        Assert.Single(partitions);
    }

    [Fact]
    public async Task PlanReadAsync_commits_with_configured_ref_never_calls_the_repo_endpoint()
    {
        var handler = new FakeHandler();
        var source = Source(handler);

        var partitions = await source.PlanReadAsync(
            new DatasetSpec("github", "acme/widgets/commits", new Dictionary<string, object?> { ["ref"] = "develop" }),
            ReadHints.None, CancellationToken.None);

        Assert.Single(partitions);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PlanReadAsync_commits_without_a_configured_ref_resolves_the_default_branch()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets", FakeHandler.Json(HttpStatusCode.OK, """{"default_branch":"trunk"}"""));
        handler.Map("/repos/acme/widgets/commits?sha=trunk&per_page=100",
            FakeHandler.Json(HttpStatusCode.OK, "[]"));
        var source = Source(handler);

        var partitions = await source.PlanReadAsync(Spec("acme/widgets/commits"), ReadHints.None, CancellationToken.None);

        Assert.Single(partitions);
        Assert.Single(handler.Requests); // only the /repos/{o}/{r} lookup so far -- reading is lazy
        Assert.Equal("/repos/acme/widgets", handler.Requests[0].PathAndQuery);

        // Reading the returned partition proves it was built with the resolved default branch.
        await foreach (var batch in partitions[0].ReadAsync(BatchOptions.Default, CancellationToken.None))
        {
            batch.Dispose();
        }

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/repos/acme/widgets/commits?sha=trunk&per_page=100", handler.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task PlanReadAsync_commits_default_branch_lookup_surfaces_the_classified_exception_on_failure()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets", FakeHandler.Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        var source = Source(handler);

        var ex = await Assert.ThrowsAsync<PzConnectorException>(() =>
            source.PlanReadAsync(Spec("acme/widgets/commits"), ReadHints.None, CancellationToken.None).AsTask());

        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task PlanReadAsync_commits_default_branch_lookup_transport_failure_surfaces_a_transient_classified_exception()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets", _ => throw new HttpRequestException("connection reset"));
        var source = Source(handler);

        var ex = await Assert.ThrowsAsync<PzConnectorException>(() =>
            source.PlanReadAsync(Spec("acme/widgets/commits"), ReadHints.None, CancellationToken.None).AsTask());

        Assert.True(ex.IsTransient);
        Assert.StartsWith("github: resolving default branch for acme/widgets:", ex.Message);
    }

    [Fact]
    public async Task DisposeAsync_completes_immediately()
    {
        var source = Source(new FakeHandler());

        await source.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_disposes_the_underlying_http_client()
    {
        var handler = new FakeHandler();
        var client = Client(handler);
        var source = new GithubSource(Connection(), client, NullLogger.Instance);

        await source.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAsync("repos/acme/widgets"));
    }
}
