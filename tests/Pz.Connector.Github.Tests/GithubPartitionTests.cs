using System.Net;
using Apache.Arrow;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github.Tests;

public sealed class GithubPartitionTests
{
    private static HttpClient Client(FakeHandler handler) => new(handler) { BaseAddress = new Uri("https://api.github.com") };

    private static DatasetSpec Spec(string dataset, string? watermarkValue = null) =>
        new("github", dataset, new Dictionary<string, object?>()) { WatermarkValue = watermarkValue };

    private static GithubPartition Partition(
        HttpClient client, GithubDatasetConfig config, string? resolvedRef, DatasetSpec spec) =>
        new(client, config, resolvedRef, spec, GithubRedactor.None, TimeProvider.System);

    private static async Task<List<RecordBatch>> DrainAsync(GithubPartition partition, BatchOptions? options = null, CancellationToken ct = default)
    {
        var batches = new List<RecordBatch>();
        await foreach (var batch in partition.ReadAsync(options ?? BatchOptions.Default, ct))
        {
            batches.Add(batch);
        }

        return batches;
    }

    // ---- Issues: since= construction, pull-request skip ----

    [Fact]
    public async Task Issues_with_watermark_builds_since_url()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets/issues?state=all&sort=updated&direction=asc&per_page=30&since=2026-01-01T00:00:00Z",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"id":1,"number":10,"title":"t","state":"open","user":{"login":"alice"},"body":null,
                  "labels":[],"assignees":[],"milestone":null,"comments":0,
                  "created_at":"2026-01-02T00:00:00Z","updated_at":"2026-01-02T00:00:00Z","closed_at":null,
                  "html_url":"https://x","pull_request":null}]
                """));

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.Issues), 30, null);
        var spec = Spec("acme/widgets/issues", "2026-01-01T00:00:00.000000");
        var partition = Partition(Client(handler), config, resolvedRef: null, spec);

        var batches = await DrainAsync(partition);

        Assert.Single(handler.Requests);
        var row = Assert.Single(batches);
        Assert.Equal(1, row.Length);
        row.Dispose();
    }

    [Fact]
    public async Task Issues_skips_pull_request_shaped_items_without_stopping()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets/issues?state=all&sort=updated&direction=asc&per_page=100",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"id":1,"number":1,"title":"pr","state":"open","user":null,"body":null,
                  "labels":[],"assignees":[],"milestone":null,"comments":0,
                  "created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","closed_at":null,
                  "html_url":"https://x","pull_request":{}},
                 {"id":2,"number":2,"title":"issue","state":"open","user":null,"body":null,
                  "labels":[],"assignees":[],"milestone":null,"comments":0,
                  "created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","closed_at":null,
                  "html_url":"https://x","pull_request":null}]
                """));

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.Issues), 100, null);
        var partition = Partition(Client(handler), config, resolvedRef: null, Spec("acme/widgets/issues"));

        var batches = await DrainAsync(partition);

        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Length);
        batch.Dispose();
    }

    // ---- Pulls: no since=, early stop on updated_at ----

    [Fact]
    public async Task Pulls_never_sends_a_since_param_even_with_a_watermark()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets/pulls?state=all&sort=updated&direction=desc&per_page=100",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"id":1,"number":1,"title":"p","state":"open","user":null,"body":null,"draft":false,
                  "base":null,"head":null,"merged_at":null,"created_at":"2026-01-05T00:00:00Z",
                  "updated_at":"2026-01-05T00:00:00Z","closed_at":null,"html_url":"https://x"}]
                """));

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.Pulls), 100, null);
        var partition = Partition(Client(handler), config, resolvedRef: null,
            Spec("acme/widgets/pulls", "2026-01-01T00:00:00.000000"));

        var batches = await DrainAsync(partition);

        Assert.Single(handler.Requests);
        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Length);
        batch.Dispose();
    }

    [Fact]
    public async Task Pulls_drops_at_or_before_watermark_items_and_stops_without_requesting_next_page()
    {
        var handler = new FakeHandler();
        // desc order: newer, then one exactly at the watermark, then one older -- both of the
        // latter two must be dropped, and the presence of either must stop the loop before page 2.
        handler.Map("/repos/acme/widgets/pulls?state=all&sort=updated&direction=desc&per_page=100",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"id":1,"number":1,"title":"newer","state":"open","user":null,"body":null,"draft":false,
                  "base":null,"head":null,"merged_at":null,"created_at":"2026-01-03T00:00:00Z",
                  "updated_at":"2026-01-03T00:00:00Z","closed_at":null,"html_url":"https://x"},
                 {"id":2,"number":2,"title":"equal","state":"open","user":null,"body":null,"draft":false,
                  "base":null,"head":null,"merged_at":null,"created_at":"2026-01-01T00:00:00Z",
                  "updated_at":"2026-01-01T00:00:00Z","closed_at":null,"html_url":"https://x"},
                 {"id":3,"number":3,"title":"older","state":"open","user":null,"body":null,"draft":false,
                  "base":null,"head":null,"merged_at":null,"created_at":"2025-12-31T00:00:00Z",
                  "updated_at":"2025-12-31T00:00:00Z","closed_at":null,"html_url":"https://x"}]
                """,
                linkHeader: "<https://api.github.com/repos/acme/widgets/pulls?state=all&sort=updated&direction=desc&per_page=100&page=2>; rel=\"next\""));
        // Deliberately not mapped: page 2 must never be requested.

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.Pulls), 100, null);
        var partition = Partition(Client(handler), config, resolvedRef: null,
            Spec("acme/widgets/pulls", "2026-01-01T00:00:00.000000"));

        var batches = await DrainAsync(partition);

        Assert.Single(handler.Requests);
        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Length);
        batch.Dispose();
    }

    // ---- Releases: same early-stop shape, on created_at, never filtered server-side ----

    [Fact]
    public async Task Releases_never_sends_a_filter_param_and_stops_on_created_at()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets/releases?per_page=100",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"id":1,"tag_name":"v2","name":"v2","body":null,"draft":false,"prerelease":false,
                  "author":null,"created_at":"2026-01-03T00:00:00Z","published_at":"2026-01-03T00:00:00Z",
                  "html_url":"https://x"},
                 {"id":2,"tag_name":"v1","name":"v1","body":null,"draft":false,"prerelease":false,
                  "author":null,"created_at":"2026-01-01T00:00:00Z","published_at":"2026-01-01T00:00:00Z",
                  "html_url":"https://x"}]
                """,
                linkHeader: "<https://api.github.com/repos/acme/widgets/releases?per_page=100&page=2>; rel=\"next\""));

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.Releases), 100, null);
        var partition = Partition(Client(handler), config, resolvedRef: null,
            Spec("acme/widgets/releases", "2026-01-01T00:00:00.000000"));

        var batches = await DrainAsync(partition);

        Assert.Single(handler.Requests);
        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Length);
        batch.Dispose();
    }

    // ---- Commits: sha=<ref>, since= ----

    [Fact]
    public async Task Commits_builds_sha_and_since_from_configured_ref()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets/commits?sha=develop&per_page=50&since=2026-01-01T00:00:00Z",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"sha":"abc123","commit":{"author":{"name":"a","email":"a@x.com","date":"2026-01-02T00:00:00Z"},
                  "committer":{"name":"a","email":"a@x.com","date":"2026-01-02T00:00:00Z"},"message":"m"},
                  "author":{"login":"alice"},"html_url":"https://x"}]
                """));

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.Commits), 50, "develop");
        var partition = Partition(Client(handler), config, resolvedRef: "develop",
            Spec("acme/widgets/commits", "2026-01-01T00:00:00.000000"));

        var batches = await DrainAsync(partition);

        Assert.Single(handler.Requests);
        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Length);
        batch.Dispose();
    }

    [Fact]
    public async Task Commits_url_escapes_a_ref_containing_hash_ampersand_and_space()
    {
        // '#' is Uri's fragment delimiter -- left unescaped, "sha=feature#123&x=y bar" would have
        // everything from '#' onward silently dropped before the request is even sent, turning into
        // a request for branch "feature" instead of the ref actually configured. '&' and the space
        // would each corrupt the query string a different way (an injected bogus parameter; an
        // invalid/truncated value). Escaping must survive all three in one ref.
        const string rawRef = "feature#123&x=y bar";
        var expectedSha = Uri.EscapeDataString(rawRef);
        var handler = new FakeHandler();
        handler.Map($"/repos/acme/widgets/commits?sha={expectedSha}&per_page=50",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"sha":"abc123","commit":{"author":{"name":"a","email":"a@x.com","date":"2026-01-02T00:00:00Z"},
                  "committer":{"name":"a","email":"a@x.com","date":"2026-01-02T00:00:00Z"},"message":"m"},
                  "author":{"login":"alice"},"html_url":"https://x"}]
                """));

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.Commits), 50, rawRef);
        var partition = Partition(Client(handler), config, resolvedRef: rawRef, Spec("acme/widgets/commits"));

        var batches = await DrainAsync(partition);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/repos/acme/widgets/commits?sha={expectedSha}&per_page=50", request.PathAndQuery);
        Assert.DoesNotContain('#', request.PathAndQuery);
        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Length);
        batch.Dispose();
    }

    // ---- IssueComments: since=, every item appended ----

    [Fact]
    public async Task IssueComments_with_watermark_builds_since_and_appends_every_item()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets/issues/comments?sort=updated&direction=asc&per_page=100&since=2026-01-01T00:00:00Z",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"id":1,"issue_url":"https://api.github.com/repos/acme/widgets/issues/42","user":{"login":"bob"},
                  "body":"hi","created_at":"2026-01-02T00:00:00Z","updated_at":"2026-01-02T00:00:00Z",
                  "html_url":"https://x"}]
                """));

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.IssueComments), 100, null);
        var partition = Partition(Client(handler), config, resolvedRef: null,
            Spec("acme/widgets/issues/comments", "2026-01-01T00:00:00.000000"));

        var batches = await DrainAsync(partition);

        Assert.Single(handler.Requests);
        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Length);
        batch.Dispose();
    }

    // ---- ActionsRuns: created=>=<iso>, server-side filtered, no client-side stop ----

    [Fact]
    public async Task ActionsRuns_builds_created_ge_param()
    {
        var expectedCreated = Uri.EscapeDataString(">=2026-01-01T00:00:00Z");
        var handler = new FakeHandler();
        handler.Map($"/repos/acme/widgets/actions/runs?per_page=100&created={expectedCreated}",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"id":1,"name":"ci","run_number":1,"head_branch":"main","head_sha":"abc","event":"push",
                  "status":"completed","conclusion":"success","created_at":"2026-01-02T00:00:00Z",
                  "updated_at":"2026-01-02T00:00:00Z","run_started_at":"2026-01-02T00:00:00Z",
                  "html_url":"https://x"}]
                """));

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.ActionsRuns), 100, null);
        var partition = Partition(Client(handler), config, resolvedRef: null,
            Spec("acme/widgets/actions/runs", "2026-01-01T00:00:00.000000"));

        var batches = await DrainAsync(partition);

        Assert.Single(handler.Requests);
        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Length);
        batch.Dispose();
    }

    // ---- Pagination: multi-page consumption when no early stop applies ----

    [Fact]
    public async Task Two_pages_are_fully_consumed_via_link_header_when_no_early_stop_applies()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets/issues/comments?sort=updated&direction=asc&per_page=100",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"id":1,"issue_url":"https://api.github.com/repos/acme/widgets/issues/1","user":null,
                  "body":"a","created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z",
                  "html_url":"https://x"}]
                """,
                linkHeader: "<https://api.github.com/repos/acme/widgets/issues/comments?sort=updated&direction=asc&per_page=100&page=2>; rel=\"next\""));
        handler.Map("/repos/acme/widgets/issues/comments?sort=updated&direction=asc&per_page=100&page=2",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"id":2,"issue_url":"https://api.github.com/repos/acme/widgets/issues/2","user":null,
                  "body":"b","created_at":"2026-01-02T00:00:00Z","updated_at":"2026-01-02T00:00:00Z",
                  "html_url":"https://x"}]
                """));

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.IssueComments), 100, null);
        var partition = Partition(Client(handler), config, resolvedRef: null, Spec("acme/widgets/issues/comments"));

        var batches = await DrainAsync(partition);

        Assert.Equal(2, handler.Requests.Count);
        var batch = Assert.Single(batches);
        Assert.Equal(2, batch.Length);
        batch.Dispose();
    }

    // ---- Batch cutting: a small MaxRowsPerBatch cuts a batch mid-page ----

    [Fact]
    public async Task Small_max_rows_per_batch_cuts_a_batch_mid_page()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets/issues?state=all&sort=updated&direction=asc&per_page=100",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"id":1,"number":1,"title":"a","state":"open","user":null,"body":null,"labels":[],
                  "assignees":[],"milestone":null,"comments":0,"created_at":"2026-01-01T00:00:00Z",
                  "updated_at":"2026-01-01T00:00:00Z","closed_at":null,"html_url":"https://x","pull_request":null},
                 {"id":2,"number":2,"title":"b","state":"open","user":null,"body":null,"labels":[],
                  "assignees":[],"milestone":null,"comments":0,"created_at":"2026-01-01T00:00:00Z",
                  "updated_at":"2026-01-01T00:00:00Z","closed_at":null,"html_url":"https://x","pull_request":null},
                 {"id":3,"number":3,"title":"c","state":"open","user":null,"body":null,"labels":[],
                  "assignees":[],"milestone":null,"comments":0,"created_at":"2026-01-01T00:00:00Z",
                  "updated_at":"2026-01-01T00:00:00Z","closed_at":null,"html_url":"https://x","pull_request":null}]
                """));

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.Issues), 100, null);
        var partition = Partition(Client(handler), config, resolvedRef: null, Spec("acme/widgets/issues"));

        var batches = await DrainAsync(partition, new BatchOptions(MaxRowsPerBatch: 2));

        Assert.Equal(2, batches.Count);
        Assert.Equal(2, batches[0].Length);
        Assert.Equal(1, batches[1].Length);
        foreach (var b in batches) { b.Dispose(); }
    }

    // ---- Errors: non-2xx surfaces the classified exception and stops the loop ----

    [Fact]
    public async Task Non_2xx_response_surfaces_the_classified_exception()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets/issues?state=all&sort=updated&direction=asc&per_page=100",
            FakeHandler.Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.Issues), 100, null);
        var partition = Partition(Client(handler), config, resolvedRef: null, Spec("acme/widgets/issues"));

        var ex = await Assert.ThrowsAsync<PzConnectorException>(() => DrainAsync(partition));

        Assert.False(ex.IsTransient);
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    // ---- Cancellation: honored between pages, before a second request is made ----

    [Fact]
    public async Task Cancellation_between_pages_stops_before_a_second_request()
    {
        var handler = new FakeHandler();
        handler.Map("/repos/acme/widgets/issues?state=all&sort=updated&direction=asc&per_page=100",
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"id":1,"number":1,"title":"a","state":"open","user":null,"body":null,"labels":[],
                  "assignees":[],"milestone":null,"comments":0,"created_at":"2026-01-01T00:00:00Z",
                  "updated_at":"2026-01-01T00:00:00Z","closed_at":null,"html_url":"https://x","pull_request":null}]
                """,
                linkHeader: "<https://api.github.com/repos/acme/widgets/issues?state=all&sort=updated&direction=asc&per_page=100&page=2>; rel=\"next\""));
        // Deliberately not mapped: a second request must never be made once cancellation is observed.

        var config = new GithubDatasetConfig(new EntityRef("acme", "widgets", GithubEntityKind.Issues), 100, null);
        var partition = Partition(Client(handler), config, resolvedRef: null, Spec("acme/widgets/issues"));

        using var cts = new CancellationTokenSource();
        // MaxRowsPerBatch: 1 forces a batch to be yielded as soon as page 1's single row is appended,
        // so the enumerator pauses (control returns to this test) before the loop would otherwise
        // proceed straight on to fetching page 2 within the same MoveNextAsync call.
        var options = new BatchOptions(MaxRowsPerBatch: 1);
        await using var enumerator = partition.ReadAsync(options, cts.Token).GetAsyncEnumerator();

        var moved = await enumerator.MoveNextAsync();
        Assert.True(moved);
        enumerator.Current.Dispose();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        Assert.Single(handler.Requests);
    }
}
