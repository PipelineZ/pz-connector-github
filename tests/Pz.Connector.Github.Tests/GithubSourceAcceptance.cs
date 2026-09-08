extern alias FakeServerAssembly;

using Apache.Arrow;
using FakeGithubServer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github.Tests;

/// <summary>End-to-end acceptance tests: the REAL connector (<see cref="GithubConnector"/> and
/// friends), driven entirely through its public surface, against the REAL fake GitHub server
/// (<c>tests/fixtures/FakeGithubServer</c>) -- bound to a real loopback Kestrel socket via
/// <see cref="WebApplicationFactory{TEntryPoint}.UseKestrel()"/> so the connector's own unmodified
/// <c>HttpClient</c> construction reaches it exactly as it would a live GitHub server. This is what
/// distinguishes these tests from the scripted-<c>FakeHandler</c> unit tests elsewhere in this
/// project: here, GitHub's actual JSON shapes, pagination headers, and `since`/watermark semantics
/// are produced by a real, independent HTTP server, not canned per-test responses.</summary>
public sealed class GithubSourceAcceptance : IAsyncLifetime
{
    private WebApplicationFactory<FakeServerAssembly::Program> _factory = null!;
    private RequestLog _requestLog = null!;
    private Uri _baseUrl = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<FakeServerAssembly::Program>();
        _factory.UseKestrel();

        // Services (or any access WebApplicationFactory needs to start the host) forces the real
        // Kestrel listener to bind before ClientOptions.BaseAddress reflects its actual address.
        _requestLog = _factory.Services.GetRequiredService<RequestLog>();
        _baseUrl = _factory.ClientOptions.BaseAddress;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ---- helpers ----

    private static ConnectorConfig Config(Uri baseUrl, string? token = null)
    {
        var values = new Dictionary<string, object?> { ["url"] = baseUrl.ToString() };
        if (token is not null)
        {
            values["token"] = token;
        }

        return new ConnectorConfig(values);
    }

    private static DatasetSpec Spec(string dataset, string? watermarkValue = null, int? perPage = null)
    {
        var options = new Dictionary<string, object?>();
        if (perPage is not null)
        {
            options["per_page"] = perPage.Value;
        }

        return new DatasetSpec("github", dataset, options) { WatermarkValue = watermarkValue };
    }

    /// <summary>Drives the real connector end-to-end for one dataset: <c>OpenAsync</c> ->
    /// <c>GetSchemaAsync</c> -> <c>PlanReadAsync</c> -> drain the one partition's <c>ReadAsync</c>,
    /// converting every Arrow row into a name-addressable dictionary. Returns the schema alongside
    /// the rows so callers can assert on field presence too.</summary>
    private async Task<(Schema Schema, List<Dictionary<string, object?>> Rows)> ReadAllAsync(
        string dataset, string? watermarkValue = null, int? perPage = null, string? token = null)
    {
        ISourceConnector connector = new GithubConnector();
        var source = await connector.OpenAsync(Config(_baseUrl, token), CancellationToken.None);
        try
        {
            var spec = Spec(dataset, watermarkValue, perPage);
            var schema = await source.GetSchemaAsync(spec, CancellationToken.None);
            var partitions = await source.PlanReadAsync(spec, ReadHints.None, CancellationToken.None);
            Assert.Single(partitions);

            var rows = new List<Dictionary<string, object?>>();
            await foreach (var batch in partitions[0].ReadAsync(BatchOptions.Default, CancellationToken.None))
            {
                using (batch)
                {
                    for (var r = 0; r < batch.Length; r++)
                    {
                        var row = new Dictionary<string, object?>(schema.Schema.FieldsList.Count);
                        for (var c = 0; c < schema.Schema.FieldsList.Count; c++)
                        {
                            row[schema.Schema.FieldsList[c].Name] = GetValue(batch.Column(c), r);
                        }

                        rows.Add(row);
                    }
                }
            }

            return (schema.Schema, rows);
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    private static object? GetValue(IArrowArray array, int index) => array switch
    {
        Int64Array a => a.GetValue(index),
        Int32Array a => a.GetValue(index),
        BooleanArray a => a.GetValue(index),
        StringArray a => a.IsNull(index) ? null : a.GetString(index),
        TimestampArray a => a.GetTimestamp(index),
        _ => throw new NotSupportedException($"unsupported array type {array.GetType()}"),
    };

    // ---- full read per kind ----

    [Fact]
    public async Task Issues_full_read_returns_every_real_issue_skipping_pull_request_shaped_ones()
    {
        var (schema, rows) = await ReadAllAsync("fixture/repo/issues");

        Assert.True(schema.FieldsList.Count > 0);
        Assert.Equal(27, rows.Count); // 30 seeded - 3 pull-request-shaped items the connector skips

        var expected = FixtureRepo.Issues[0];
        var row = Assert.Single(rows, r => (long)r["id"]! == expected.Id);
        Assert.Equal(expected.Number, row["number"]);
        Assert.Equal(expected.Title, row["title"]);
        Assert.Equal(expected.State, row["state"]);
        Assert.Equal(expected.User.Login, row["author"]);
        Assert.Equal("""["bug","area-0"]""", row["labels"]);
        Assert.Equal("""["user0"]""", row["assignees"]);
        Assert.Equal(expected.Milestone!.Title, row["milestone"]);

        // None of the 27 rows is one of the fixture's pull-request-shaped issue ids.
        var prShapedIds = new HashSet<long>([FixtureRepo.Issues[5].Id, FixtureRepo.Issues[15].Id, FixtureRepo.Issues[25].Id]);
        Assert.DoesNotContain(rows, r => prShapedIds.Contains((long)r["id"]!));
    }

    [Fact]
    public async Task Pulls_full_read_returns_every_pull()
    {
        var (_, rows) = await ReadAllAsync("fixture/repo/pulls");

        Assert.Equal(FixtureRepo.RowCount, rows.Count);

        var expected = FixtureRepo.Pulls[3];
        var row = Assert.Single(rows, r => (long)r["id"]! == expected.Id);
        Assert.Equal(expected.Title, row["title"]);
        Assert.Equal(expected.Base.Ref, row["base_ref"]);
        Assert.Equal(expected.Head.Ref, row["head_ref"]);
        Assert.Equal(expected.Draft, row["draft"]);
    }

    [Fact]
    public async Task IssueComments_full_read_returns_every_comment()
    {
        var (_, rows) = await ReadAllAsync("fixture/repo/issues/comments");

        Assert.Equal(FixtureRepo.RowCount, rows.Count);

        var expected = FixtureRepo.Comments[7];
        var row = Assert.Single(rows, r => (long)r["id"]! == expected.Id);
        Assert.Equal(expected.Body, row["body"]);
        Assert.Equal(expected.User.Login, row["author"]);
    }

    [Fact]
    public async Task Commits_full_read_returns_every_commit()
    {
        var (_, rows) = await ReadAllAsync("fixture/repo/commits");

        Assert.Equal(FixtureRepo.RowCount, rows.Count);

        var expected = FixtureRepo.Commits[9];
        var row = Assert.Single(rows, r => (string)r["sha"]! == expected.Sha);
        Assert.Equal(expected.Commit.Message, row["message"]);
        Assert.Equal(expected.Commit.Author.Name, row["author_name"]);
        Assert.Equal(expected.Commit.Author.Email, row["author_email"]);
        Assert.Equal(expected.Author!.Login, row["author_login"]);
    }

    [Fact]
    public async Task Releases_full_read_returns_every_release()
    {
        var (_, rows) = await ReadAllAsync("fixture/repo/releases");

        Assert.Equal(FixtureRepo.RowCount, rows.Count);

        var expected = FixtureRepo.Releases[1];
        var row = Assert.Single(rows, r => (long)r["id"]! == expected.Id);
        Assert.Equal(expected.TagName, row["tag_name"]);
        Assert.Equal(expected.Draft, row["draft"]);
        Assert.Equal(expected.Prerelease, row["prerelease"]);
    }

    [Fact]
    public async Task ActionsRuns_full_read_returns_every_run()
    {
        var (_, rows) = await ReadAllAsync("fixture/repo/actions/runs");

        Assert.Equal(FixtureRepo.RowCount, rows.Count);

        var expected = FixtureRepo.Runs[2];
        var row = Assert.Single(rows, r => (long)r["id"]! == expected.Id);
        Assert.Equal(expected.Status, row["status"]);
        Assert.Equal(expected.Event, row["event"]);
        Assert.Equal(expected.HeadBranch, row["head_branch"]);
    }

    // ---- incremental read per kind ----
    //
    // Watermark = the 15th row's (index 14, 0-based) timestamp: 2026-01-15T00:00:00Z. Server-side
    // `since` filters (issues, comments, commits) are strictly-greater, matching GitHub's own
    // documented "updated/committed after this time" semantics, so index 14 itself is excluded --
    // rows 15..29 (15 rows) survive, minus the 2 pull-request-shaped issues among them (15, 25).
    // Actions runs uses `created=>=<iso>`, an inclusive lower bound, so index 14 itself survives:
    // rows 14..29 (16 rows). Pulls/releases apply the watermark client-side with the same
    // strictly-greater rule as the server-side kinds (rows 15..29, 15 rows).

    [Fact]
    public async Task Issues_incremental_read_returns_only_rows_newer_than_the_watermark()
    {
        var watermark = FixtureRepo.TimestampAt(14).ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
        var (_, rows) = await ReadAllAsync("fixture/repo/issues", watermarkValue: watermark);

        Assert.Equal(13, rows.Count); // 15 newer issues, minus the 2 pull-request-shaped ones among them
        Assert.All(rows, r => Assert.True(((DateTimeOffset)r["updated_at"]!) > FixtureRepo.TimestampAt(14)));
    }

    [Fact]
    public async Task IssueComments_incremental_read_returns_only_rows_newer_than_the_watermark()
    {
        var watermark = FixtureRepo.TimestampAt(14).ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
        var (_, rows) = await ReadAllAsync("fixture/repo/issues/comments", watermarkValue: watermark);

        Assert.Equal(15, rows.Count);
        Assert.All(rows, r => Assert.True(((DateTimeOffset)r["updated_at"]!) > FixtureRepo.TimestampAt(14)));
    }

    [Fact]
    public async Task Commits_incremental_read_returns_only_rows_newer_than_the_watermark()
    {
        var watermark = FixtureRepo.TimestampAt(14).ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
        var (_, rows) = await ReadAllAsync("fixture/repo/commits", watermarkValue: watermark);

        Assert.Equal(15, rows.Count);
        Assert.All(rows, r => Assert.True(((DateTimeOffset)r["committed_at"]!) > FixtureRepo.TimestampAt(14)));
    }

    [Fact]
    public async Task ActionsRuns_incremental_read_returns_rows_at_or_after_the_watermark()
    {
        var watermark = FixtureRepo.TimestampAt(14).ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
        var (_, rows) = await ReadAllAsync("fixture/repo/actions/runs", watermarkValue: watermark);

        Assert.Equal(16, rows.Count); // inclusive: the `created=>=` server filter keeps index 14 itself
        Assert.All(rows, r => Assert.True(((DateTimeOffset)r["created_at"]!) >= FixtureRepo.TimestampAt(14)));
    }

    [Fact]
    public async Task Pulls_incremental_read_stops_early_without_exhausting_every_page()
    {
        var watermark = FixtureRepo.TimestampAt(14).ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
        var before = _requestLog.Count;

        var (_, rows) = await ReadAllAsync("fixture/repo/pulls", watermarkValue: watermark, perPage: 2);

        Assert.Equal(15, rows.Count);
        Assert.All(rows, r => Assert.True(((DateTimeOffset)r["updated_at"]!) > FixtureRepo.TimestampAt(14)));

        // A full, non-early-stopped read of 30 rows at per_page=2 would need 15 requests; the
        // client-side early stop must cut this well short of that.
        var requestsMade = _requestLog.Count - before;
        Assert.True(requestsMade < 15, $"expected an early-stopped page count, got {requestsMade} requests");
        Assert.Equal(8, requestsMade);
    }

    [Fact]
    public async Task Releases_incremental_read_stops_early_without_exhausting_every_page()
    {
        var watermark = FixtureRepo.TimestampAt(14).ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
        var before = _requestLog.Count;

        var (_, rows) = await ReadAllAsync("fixture/repo/releases", watermarkValue: watermark, perPage: 2);

        Assert.Equal(15, rows.Count);
        Assert.All(rows, r => Assert.True(((DateTimeOffset)r["created_at"]!) > FixtureRepo.TimestampAt(14)));

        var requestsMade = _requestLog.Count - before;
        Assert.True(requestsMade < 15, $"expected an early-stopped page count, got {requestsMade} requests");
        Assert.Equal(8, requestsMade);
    }

    // ---- paging to completion ----

    [Fact]
    public async Task Issues_per_page_two_pages_to_completion()
    {
        var before = _requestLog.Count;

        var (_, rows) = await ReadAllAsync("fixture/repo/issues", perPage: 2);

        Assert.Equal(27, rows.Count);
        Assert.Equal(15, _requestLog.Count - before); // ceil(30 / 2)
        Assert.Equal(27, rows.Select(r => r["id"]).Distinct().Count()); // every row is distinct, none dropped/duplicated
    }

    // ---- forced-status classification ----
    //
    // Each case drives the connector's own default-branch lookup (a Commits dataset with no
    // `ref:`, whose request URL -- `/repos/{owner}/{repo}` -- carries no query string of its own)
    // with a repo name that embeds `?force_status=...`; once the connector's request string is
    // parsed as a URI, that becomes exactly a `force_status` query parameter, so the fake server's
    // short-circuit responds before ever consulting fixture data. This is the only entity-addressing
    // surface the connector exposes with no query string already appended, so it is the one place
    // an acceptance test can reach every forced-status route through the real connector rather than
    // by calling the fake server's HTTP endpoint directly.

    private async Task<PzConnectorException> ForcedStatusAsync(string repoSuffix)
    {
        ISourceConnector connector = new GithubConnector();
        var source = await connector.OpenAsync(Config(_baseUrl), CancellationToken.None);
        try
        {
            var spec = Spec($"fixture/{repoSuffix}/commits");
            return await Assert.ThrowsAsync<PzConnectorException>(() =>
                source.PlanReadAsync(spec, ReadHints.None, CancellationToken.None).AsTask());
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task ForcedStatus_429_is_not_transient_but_carries_the_retry_after_header()
    {
        var ex = await ForcedStatusAsync("repo?force_status=429");

        Assert.False(ex.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);
        Assert.Contains("You have exceeded a rate limit", ex.Message);
    }

    [Fact]
    public async Task ForcedStatus_403_with_remaining_zero_is_transient_with_a_reset_based_retry_after()
    {
        var ex = await ForcedStatusAsync("repo?force_status=403&remaining=0");

        Assert.True(ex.IsTransient);
        Assert.NotNull(ex.RetryAfter);
        Assert.True(ex.RetryAfter > TimeSpan.Zero && ex.RetryAfter <= TimeSpan.FromSeconds(30),
            $"expected RetryAfter in (0s, 30s], got {ex.RetryAfter}");
        Assert.Contains("rate limited (HTTP 403)", ex.Message);
    }

    [Fact]
    public async Task ForcedStatus_401_is_not_transient_and_carries_no_retry_after()
    {
        var ex = await ForcedStatusAsync("repo?force_status=401");

        Assert.False(ex.IsTransient);
        Assert.Null(ex.RetryAfter);
        Assert.Contains("unauthorized (HTTP 401)", ex.Message);
    }

    [Fact]
    public async Task ForcedStatus_404_is_not_transient_and_carries_no_retry_after()
    {
        var ex = await ForcedStatusAsync("repo?force_status=404");

        Assert.False(ex.IsTransient);
        Assert.Null(ex.RetryAfter);
        Assert.Contains("not found (HTTP 404)", ex.Message);
    }

    [Fact]
    public async Task ForcedStatus_422_is_not_transient_and_surfaces_the_validation_message()
    {
        var ex = await ForcedStatusAsync("repo?force_status=422");

        Assert.False(ex.IsTransient);
        Assert.Null(ex.RetryAfter);
        Assert.Contains("Validation Failed", ex.Message);
    }

    // ---- CheckConnectionAsync ----

    [Fact]
    public async Task CheckConnectionAsync_authenticated_against_the_fixture_succeeds()
    {
        var connector = new GithubConnector();

        var check = await connector.CheckConnectionAsync(Config(_baseUrl, FixtureRepo.ExpectedToken), CancellationToken.None);

        Assert.True(check.Ok);
        Assert.Equal("authenticated as fixture-user, 4999/5000 requests remaining", check.Message);
    }

    [Fact]
    public async Task CheckConnectionAsync_unauthenticated_against_the_fixture_succeeds()
    {
        var connector = new GithubConnector();

        var check = await connector.CheckConnectionAsync(Config(_baseUrl), CancellationToken.None);

        Assert.True(check.Ok);
        Assert.Equal("unauthenticated, 4999/5000 requests remaining", check.Message);
    }

    [Fact]
    public async Task CheckConnectionAsync_wrong_token_fails_without_throwing()
    {
        var connector = new GithubConnector();

        var check = await connector.CheckConnectionAsync(Config(_baseUrl, "wrong-token"), CancellationToken.None);

        Assert.False(check.Ok);
        Assert.NotNull(check.Message);
        Assert.Contains("Bad credentials", check.Message);
        Assert.DoesNotContain("wrong-token", check.Message);
    }
}
