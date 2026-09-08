using System.Globalization;
using System.Text.Json.Serialization;

namespace FakeGithubServer;

// The wire-shape records below are a deliberately independent re-implementation of "what GitHub
// sends" -- they do not reuse Pz.Connector.Github's own DTOs (IssueDto, PullDto, ...), so a bug
// shared between the connector's parsing and this fixture's serialization can't cancel itself out
// undetected. Every property name is pinned via JsonPropertyName to GitHub's actual snake_case
// field names; System.Text.Json's default (reflection-based) serializer is fine here since this
// project is a test fixture, never AOT-published.

public sealed record WireUser([property: JsonPropertyName("login")] string Login);

public sealed record WireLabel([property: JsonPropertyName("name")] string Name);

public sealed record WireMilestone([property: JsonPropertyName("title")] string? Title);

public sealed record WirePullRef([property: JsonPropertyName("ref")] string Ref);

public sealed record WireIssue(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("user")] WireUser User,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("labels")] IReadOnlyList<WireLabel> Labels,
    [property: JsonPropertyName("assignees")] IReadOnlyList<WireUser> Assignees,
    [property: JsonPropertyName("milestone")] WireMilestone? Milestone,
    [property: JsonPropertyName("comments")] int Comments,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt,
    [property: JsonPropertyName("closed_at")] string? ClosedAt,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("pull_request")] object? PullRequest)
{
    /// <summary>Not serialized -- the sort/filter key every query method compares against, kept in
    /// sync with <see cref="UpdatedAt"/> at construction time so filtering never has to re-parse the
    /// wire string.</summary>
    [JsonIgnore]
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record WirePull(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("user")] WireUser User,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("base")] WirePullRef Base,
    [property: JsonPropertyName("head")] WirePullRef Head,
    [property: JsonPropertyName("merged_at")] string? MergedAt,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt,
    [property: JsonPropertyName("closed_at")] string? ClosedAt,
    [property: JsonPropertyName("html_url")] string HtmlUrl)
{
    [JsonIgnore]
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record WireIssueComment(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("issue_url")] string IssueUrl,
    [property: JsonPropertyName("user")] WireUser User,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt,
    [property: JsonPropertyName("html_url")] string HtmlUrl)
{
    [JsonIgnore]
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record WireGitUser(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("date")] string Date);

public sealed record WireCommitDetail(
    [property: JsonPropertyName("author")] WireGitUser Author,
    [property: JsonPropertyName("committer")] WireGitUser Committer,
    [property: JsonPropertyName("message")] string Message);

public sealed record WireCommit(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("commit")] WireCommitDetail Commit,
    [property: JsonPropertyName("author")] WireUser? Author,
    [property: JsonPropertyName("html_url")] string HtmlUrl)
{
    [JsonIgnore]
    public DateTimeOffset CommittedAtUtc { get; init; }
}

public sealed record WireRelease(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("author")] WireUser Author,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("published_at")] string? PublishedAt,
    [property: JsonPropertyName("html_url")] string HtmlUrl)
{
    [JsonIgnore]
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record WireActionsRun(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("run_number")] int RunNumber,
    [property: JsonPropertyName("head_branch")] string HeadBranch,
    [property: JsonPropertyName("head_sha")] string HeadSha,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conclusion")] string? Conclusion,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt,
    [property: JsonPropertyName("run_started_at")] string RunStartedAt,
    [property: JsonPropertyName("html_url")] string HtmlUrl)
{
    [JsonIgnore]
    public DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>The one in-memory GitHub repo ("fixture/repo") this fake server serves. Each kind's
/// list is seeded with <see cref="RowCount"/> rows, one per day counting forward from
/// <see cref="ReferenceDate"/>, so index order is also timestamp order across every kind --
/// <see cref="TimestampAt"/> lets a test pick a watermark that lands mid-history without reaching
/// into the seed lists directly.</summary>
public static class FixtureRepo
{
    public const string Owner = "fixture";
    public const string Repo = "repo";
    public const string DefaultBranch = "main";

    /// <summary>The only Authorization value this server accepts besides no header at all.</summary>
    public const string ExpectedToken = "fixture-test-token";

    public const int RowCount = 30;

    private static readonly DateTimeOffset ReferenceDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Row indices (0-based, oldest first) whose issues-list item is actually
    /// pull-request-shaped -- carries a non-null `pull_request` field so `IssuesSchema.TryToRow`
    /// has something real to skip. 27 of the 30 issue rows are real issues, comfortably over the
    /// "&gt;= 25 real rows" bar.</summary>
    private static readonly HashSet<int> PullRequestShapedIssueIndices = [5, 15, 25];

    public static readonly IReadOnlyList<WireIssue> Issues = BuildIssues();
    public static readonly IReadOnlyList<WirePull> Pulls = BuildPulls();
    public static readonly IReadOnlyList<WireIssueComment> Comments = BuildComments();
    public static readonly IReadOnlyList<WireCommit> Commits = BuildCommits();
    public static readonly IReadOnlyList<WireRelease> Releases = BuildReleases();
    public static readonly IReadOnlyList<WireActionsRun> Runs = BuildRuns();

    /// <summary>The Nth row's timestamp (0-based, oldest first) -- the same value across every kind,
    /// since every kind is seeded on the same one-day-per-index schedule.</summary>
    public static DateTimeOffset TimestampAt(int index) => ReferenceDate.AddDays(index);

    public static string Iso(DateTimeOffset value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseIso(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static IReadOnlyList<WireIssue> QueryIssues(string? since, string direction)
    {
        IEnumerable<WireIssue> query = Issues;
        if (since is not null)
        {
            var sinceAt = ParseIso(since);
            query = query.Where(i => i.UpdatedAtUtc > sinceAt);
        }

        return Order(query, i => i.UpdatedAtUtc, direction).ToList();
    }

    public static IReadOnlyList<WirePull> QueryPulls(string direction) =>
        Order(Pulls, p => p.UpdatedAtUtc, direction).ToList();

    public static IReadOnlyList<WireIssueComment> QueryComments(string? since, string direction)
    {
        IEnumerable<WireIssueComment> query = Comments;
        if (since is not null)
        {
            var sinceAt = ParseIso(since);
            query = query.Where(c => c.UpdatedAtUtc > sinceAt);
        }

        return Order(query, c => c.UpdatedAtUtc, direction).ToList();
    }

    /// <summary>GitHub's real commits endpoint ignores `sha` for filtering beyond selecting the
    /// branch tip to walk from; this fixture has one linear history, so every `sha` value serves the
    /// same list.</summary>
    public static IReadOnlyList<WireCommit> QueryCommits(string? since)
    {
        IEnumerable<WireCommit> query = Commits;
        if (since is not null)
        {
            var sinceAt = ParseIso(since);
            query = query.Where(c => c.CommittedAtUtc > sinceAt);
        }

        return query.OrderByDescending(c => c.CommittedAtUtc).ToList();
    }

    /// <summary>Releases are always served newest-first, matching GitHub's real default -- the
    /// connector never sends a `direction` for this endpoint and relies on that default itself for
    /// its client-side early stop.</summary>
    public static IReadOnlyList<WireRelease> QueryReleases() =>
        Releases.OrderByDescending(r => r.CreatedAtUtc).ToList();

    /// <summary>Honors only the one form the connector ever sends: `&gt;=&lt;iso&gt;` (already
    /// URL-decoded by ASP.NET Core's query binder by the time this reads it). Any other value is
    /// treated as no filter.</summary>
    public static IReadOnlyList<WireActionsRun> QueryRuns(string? createdFilter)
    {
        IEnumerable<WireActionsRun> query = Runs;
        if (createdFilter is not null && createdFilter.StartsWith(">=", StringComparison.Ordinal))
        {
            var boundary = ParseIso(createdFilter[2..]);
            query = query.Where(r => r.CreatedAtUtc >= boundary);
        }

        return query.OrderByDescending(r => r.CreatedAtUtc).ToList();
    }

    private static IEnumerable<T> Order<T>(IEnumerable<T> source, Func<T, DateTimeOffset> key, string direction) =>
        string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase)
            ? source.OrderByDescending(key)
            : source.OrderBy(key);

    private static WireUser UserAt(int index) => new($"user{index}");

    private static IReadOnlyList<WireIssue> BuildIssues()
    {
        var items = new List<WireIssue>(RowCount);
        for (var i = 0; i < RowCount; i++)
        {
            var at = TimestampAt(i);
            var isPrShaped = PullRequestShapedIssueIndices.Contains(i);
            items.Add(new WireIssue(
                Id: 1000 + i,
                Number: i + 1,
                Title: isPrShaped ? $"pr-shaped issue {i}" : $"issue {i}",
                State: i % 4 == 0 ? "closed" : "open",
                User: UserAt(i),
                Body: $"body of issue {i}",
                Labels: [new WireLabel("bug"), new WireLabel($"area-{i % 3}")],
                Assignees: [UserAt(i)],
                Milestone: i % 5 == 0 ? new WireMilestone($"milestone {i / 5}") : null,
                Comments: i % 3,
                CreatedAt: Iso(at),
                UpdatedAt: Iso(at),
                ClosedAt: i % 4 == 0 ? Iso(at) : null,
                HtmlUrl: $"https://github.com/{Owner}/{Repo}/issues/{i + 1}",
                PullRequest: isPrShaped ? new { } : null)
            {
                UpdatedAtUtc = at,
            });
        }

        return items;
    }

    private static IReadOnlyList<WirePull> BuildPulls()
    {
        var items = new List<WirePull>(RowCount);
        for (var i = 0; i < RowCount; i++)
        {
            var at = TimestampAt(i);
            items.Add(new WirePull(
                Id: 2000 + i,
                Number: i + 1,
                Title: $"pull {i}",
                State: i % 4 == 0 ? "closed" : "open",
                User: UserAt(i),
                Body: $"body of pull {i}",
                Draft: i % 6 == 0,
                Base: new WirePullRef("main"),
                Head: new WirePullRef($"feature-{i}"),
                MergedAt: i % 4 == 0 ? Iso(at) : null,
                CreatedAt: Iso(at),
                UpdatedAt: Iso(at),
                ClosedAt: i % 4 == 0 ? Iso(at) : null,
                HtmlUrl: $"https://github.com/{Owner}/{Repo}/pull/{i + 1}")
            {
                UpdatedAtUtc = at,
            });
        }

        return items;
    }

    private static IReadOnlyList<WireIssueComment> BuildComments()
    {
        var items = new List<WireIssueComment>(RowCount);
        for (var i = 0; i < RowCount; i++)
        {
            var at = TimestampAt(i);
            items.Add(new WireIssueComment(
                Id: 3000 + i,
                IssueUrl: $"https://api.github.com/repos/{Owner}/{Repo}/issues/{(i % 10) + 1}",
                User: UserAt(i),
                Body: $"comment {i}",
                CreatedAt: Iso(at),
                UpdatedAt: Iso(at),
                HtmlUrl: $"https://github.com/{Owner}/{Repo}/issues/{(i % 10) + 1}#issuecomment-{3000 + i}")
            {
                UpdatedAtUtc = at,
            });
        }

        return items;
    }

    private static IReadOnlyList<WireCommit> BuildCommits()
    {
        var items = new List<WireCommit>(RowCount);
        for (var i = 0; i < RowCount; i++)
        {
            var at = TimestampAt(i);
            var sha = i.ToString("x40", CultureInfo.InvariantCulture);
            items.Add(new WireCommit(
                Sha: sha,
                Commit: new WireCommitDetail(
                    Author: new WireGitUser($"Author {i}", $"author{i}@example.com", Iso(at)),
                    Committer: new WireGitUser($"Author {i}", $"author{i}@example.com", Iso(at)),
                    Message: $"commit {i}"),
                Author: UserAt(i),
                HtmlUrl: $"https://github.com/{Owner}/{Repo}/commit/{sha}")
            {
                CommittedAtUtc = at,
            });
        }

        return items;
    }

    private static IReadOnlyList<WireRelease> BuildReleases()
    {
        var items = new List<WireRelease>(RowCount);
        for (var i = 0; i < RowCount; i++)
        {
            var at = TimestampAt(i);
            var isDraft = i % 7 == 0;
            items.Add(new WireRelease(
                Id: 4000 + i,
                TagName: $"v0.{i}.0",
                Name: $"release {i}",
                Body: $"notes for release {i}",
                Draft: isDraft,
                Prerelease: i % 3 == 0,
                Author: UserAt(i),
                CreatedAt: Iso(at),
                PublishedAt: isDraft ? null : Iso(at),
                HtmlUrl: $"https://github.com/{Owner}/{Repo}/releases/tag/v0.{i}.0")
            {
                CreatedAtUtc = at,
            });
        }

        return items;
    }

    private static IReadOnlyList<WireActionsRun> BuildRuns()
    {
        var items = new List<WireActionsRun>(RowCount);
        for (var i = 0; i < RowCount; i++)
        {
            var at = TimestampAt(i);
            var inProgress = i == RowCount - 1;
            items.Add(new WireActionsRun(
                Id: 5000 + i,
                Name: "CI",
                RunNumber: i + 1,
                HeadBranch: "main",
                HeadSha: i.ToString("x40", CultureInfo.InvariantCulture),
                Event: i % 2 == 0 ? "push" : "pull_request",
                Status: inProgress ? "in_progress" : "completed",
                Conclusion: inProgress ? null : (i % 5 == 0 ? "failure" : "success"),
                CreatedAt: Iso(at),
                UpdatedAt: Iso(at),
                RunStartedAt: Iso(at),
                HtmlUrl: $"https://github.com/{Owner}/{Repo}/actions/runs/{5000 + i}")
            {
                CreatedAtUtc = at,
            });
        }

        return items;
    }
}
