using System.Diagnostics;
using System.Net;
using Pz.Connectors.Abstractions;
using Pz.Connectors.Sdk;

namespace Pz.Connector.Github.Tests;

public sealed class GithubTelemetryTests
{
    private const string IssuesUrl = "/repos/acme/widgets/issues?state=all&sort=updated&direction=asc&per_page=30";
    private const string Page2 = "/repos/acme/widgets/issues?page=2";

    private static HttpResponseMessage Page(HttpStatusCode status, string? used, string? next = null)
    {
        var response = FakeHandler.Json(status, "[]", next is null ? null : $"<https://api.github.com{next}>; rel=\"next\"");
        if (used is not null)
        {
            response.Headers.Add("x-ratelimit-used", used);
        }

        response.Headers.Add("x-ratelimit-remaining", "4000");
        return response;
    }

    private static GithubPartition Partition(HttpClient client) =>
        new(client, new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.Issues), 30, null),
            null, new DatasetSpec("github", "acme/widgets/issues", new Dictionary<string, object?>()),
            GithubRedactor.None, TimeProvider.System);

    private static async Task DrainAsync(GithubPartition partition)
    {
        await foreach (var _ in partition.ReadAsync(BatchOptions.Default, CancellationToken.None))
        {
        }
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://api.github.com") };

    [Fact]
    public async Task Rate_limit_used_delta_between_pages_is_reported_as_requests_cost()
    {
        var handler = new FakeHandler();
        handler.Map(IssuesUrl, Page(HttpStatusCode.OK, "5", Page2));
        handler.Map(Page2, Page(HttpStatusCode.OK, "7"));
        var acc = new ConnectorStatsAccumulator();

        using (ConnectorStats.Use(acc))
        {
            await DrainAsync(Partition(Client(handler)));
        }

        var s = acc.Snapshot();
        Assert.Equal(2, s.CostUnits);
        Assert.Equal("requests", s.CostUnit);
        Assert.Empty(s.Extras);
    }

    [Fact]
    public async Task A_used_counter_that_went_backwards_or_is_absent_reports_no_cost()
    {
        var handler = new FakeHandler();
        handler.Map(IssuesUrl, Page(HttpStatusCode.OK, "9", Page2));
        handler.Map(Page2, Page(HttpStatusCode.OK, "1"));
        var acc = new ConnectorStatsAccumulator();

        using (ConnectorStats.Use(acc))
        {
            await DrainAsync(Partition(Client(handler)));
        }

        Assert.Null(acc.Snapshot().CostUnit);

        var none = new FakeHandler();
        none.Map(IssuesUrl, Page(HttpStatusCode.OK, null, Page2));
        none.Map(Page2, Page(HttpStatusCode.OK, "not-a-number"));
        var acc2 = new ConnectorStatsAccumulator();
        using (ConnectorStats.Use(acc2))
        {
            await DrainAsync(Partition(Client(none)));
        }

        Assert.Null(acc2.Snapshot().CostUnit);
    }

    [Fact]
    public async Task Secondary_limit_403_with_retry_after_counts_one_throttle()
    {
        var handler = new FakeHandler();
        var forbidden = FakeHandler.Json(HttpStatusCode.Forbidden, """{"message":"secondary rate limit"}""");
        forbidden.Headers.Add("Retry-After", "30");
        handler.Map(IssuesUrl, forbidden);
        var acc = new ConnectorStatsAccumulator();

        using (ConnectorStats.Use(acc))
        {
            await Assert.ThrowsAsync<PzConnectorException>(() => DrainAsync(Partition(Client(handler))));
        }

        Assert.Equal(1, acc.Snapshot().Throttled);
    }

    [Fact]
    public async Task A_plain_403_is_not_a_throttle()
    {
        var handler = new FakeHandler();
        handler.Map(IssuesUrl, FakeHandler.Json(HttpStatusCode.Forbidden, """{"message":"Resource not accessible"}"""));
        var acc = new ConnectorStatsAccumulator();

        using (ConnectorStats.Use(acc))
        {
            await Assert.ThrowsAsync<PzConnectorException>(() => DrainAsync(Partition(Client(handler))));
        }

        Assert.Equal(0, acc.Snapshot().Throttled);
    }

    [Fact]
    public async Task A_429_is_counted_once_by_the_sdk_handler_and_not_again_by_the_connector()
    {
        var handler = new FakeHandler();
        handler.Map(IssuesUrl, FakeHandler.Json(HttpStatusCode.TooManyRequests, """{"message":"slow down"}"""));
        using var source = new ActivitySource("test");
        var remote = new RemoteCalls(source);
        var acc = new ConnectorStatsAccumulator();

        using (ConnectorStats.Use(acc))
        {
            await Assert.ThrowsAsync<PzConnectorException>(() => DrainAsync(Partition(Client(remote.HttpHandler(handler)))));
        }

        var s = acc.Snapshot();
        Assert.Equal(1, s.Throttled);
        Assert.Equal(1, s.Requests);
    }
}
