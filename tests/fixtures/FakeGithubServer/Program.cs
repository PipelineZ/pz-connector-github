using System.Globalization;
using FakeGithubServer;
using Microsoft.AspNetCore.WebUtilities;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RequestLog>();
var app = builder.Build();

// Forced-status short-circuit: any request carrying `?force_status=N` gets that status back
// immediately, before auth or the default rate-limit headers -- applied uniformly, to every route,
// exactly like the spec asks. This is the only way an acceptance test can drive a specific GitHub
// failure shape through the *real* connector: none of the connector's own read-option surface
// (entity/per_page/ref) lets a caller inject an arbitrary extra query parameter into a built URL,
// but the connector's default-branch lookup (`/repos/{owner}/{repo}`, used for a Commits dataset
// with no explicit `ref:`) builds its URL with no query string of its own, so a repo name carrying
// a literal `?force_status=...` becomes exactly that query parameter once the connector's request
// string is parsed as a URI.
app.Use(async (context, next) =>
{
    if (context.Request.Query.TryGetValue("force_status", out var forced)
        && int.TryParse(forced, NumberStyles.Integer, CultureInfo.InvariantCulture, out var forcedStatus))
    {
        await WriteForcedStatusAsync(context, forcedStatus);
        return;
    }

    await next();
});

// Every response that isn't a forced-status short-circuit carries the default rate-limit headers,
// and every request (forced-status ones included, for inspection convenience) is recorded.
app.Use(async (context, next) =>
{
    context.RequestServices.GetRequiredService<RequestLog>()
        .Record(context.Request.Path.Value + context.Request.QueryString.Value);

    context.Response.OnStarting(() =>
    {
        context.Response.Headers.TryAdd("x-ratelimit-remaining", "4999");
        context.Response.Headers.TryAdd("x-ratelimit-reset",
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    });

    await next();
});

// Auth check, applied uniformly: no Authorization header at all is a valid "unauthenticated" call
// (GitHub allows unauthenticated reads at a lower rate limit); an Authorization header present but
// not exactly the fixture's expected bearer token is rejected.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue("Authorization", out var authHeader)
        && authHeader.ToString() != $"Bearer {FixtureRepo.ExpectedToken}")
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { message = "Bad credentials" });
        return;
    }

    await next();
});

app.MapGet("/rate_limit", () => Results.Json(new
{
    rate = new { limit = 5000, remaining = 4999, reset = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
}));

app.MapGet("/user", () => Results.Json(new { login = "fixture-user" }));

app.MapGet("/repos/{owner}/{repo}", (string owner, string repo) =>
    IsFixtureRepo(owner, repo)
        ? Results.Json(new { default_branch = FixtureRepo.DefaultBranch })
        : NotFound());

app.MapGet("/repos/{owner}/{repo}/issues", (HttpContext ctx, string owner, string repo) =>
{
    if (!IsFixtureRepo(owner, repo))
    {
        return NotFound();
    }

    var (page, perPage) = ReadPaging(ctx, defaultPerPage: 30);
    var since = ctx.Request.Query["since"].FirstOrDefault();
    var direction = ctx.Request.Query["direction"].FirstOrDefault() ?? "asc";
    return PagedJson(ctx, FixtureRepo.QueryIssues(since, direction), page, perPage);
});

app.MapGet("/repos/{owner}/{repo}/pulls", (HttpContext ctx, string owner, string repo) =>
{
    if (!IsFixtureRepo(owner, repo))
    {
        return NotFound();
    }

    var (page, perPage) = ReadPaging(ctx, defaultPerPage: 30);
    var direction = ctx.Request.Query["direction"].FirstOrDefault() ?? "desc";
    return PagedJson(ctx, FixtureRepo.QueryPulls(direction), page, perPage);
});

app.MapGet("/repos/{owner}/{repo}/issues/comments", (HttpContext ctx, string owner, string repo) =>
{
    if (!IsFixtureRepo(owner, repo))
    {
        return NotFound();
    }

    var (page, perPage) = ReadPaging(ctx, defaultPerPage: 30);
    var since = ctx.Request.Query["since"].FirstOrDefault();
    var direction = ctx.Request.Query["direction"].FirstOrDefault() ?? "asc";
    return PagedJson(ctx, FixtureRepo.QueryComments(since, direction), page, perPage);
});

app.MapGet("/repos/{owner}/{repo}/commits", (HttpContext ctx, string owner, string repo) =>
{
    if (!IsFixtureRepo(owner, repo))
    {
        return NotFound();
    }

    var (page, perPage) = ReadPaging(ctx, defaultPerPage: 30);
    var since = ctx.Request.Query["since"].FirstOrDefault();
    return PagedJson(ctx, FixtureRepo.QueryCommits(since), page, perPage);
});

app.MapGet("/repos/{owner}/{repo}/releases", (HttpContext ctx, string owner, string repo) =>
{
    if (!IsFixtureRepo(owner, repo))
    {
        return NotFound();
    }

    var (page, perPage) = ReadPaging(ctx, defaultPerPage: 30);
    return PagedJson(ctx, FixtureRepo.QueryReleases(), page, perPage);
});

app.MapGet("/repos/{owner}/{repo}/actions/runs", (HttpContext ctx, string owner, string repo) =>
{
    if (!IsFixtureRepo(owner, repo))
    {
        return NotFound();
    }

    var (page, perPage) = ReadPaging(ctx, defaultPerPage: 30);
    var created = ctx.Request.Query["created"].FirstOrDefault();
    return PagedJson(ctx, FixtureRepo.QueryRuns(created), page, perPage);
});

app.Run();

static bool IsFixtureRepo(string owner, string repo) => owner == FixtureRepo.Owner && repo == FixtureRepo.Repo;

static IResult NotFound() => Results.Json(new { message = "Not Found" }, statusCode: StatusCodes.Status404NotFound);

static (int Page, int PerPage) ReadPaging(HttpContext ctx, int defaultPerPage)
{
    var page = 1;
    if (int.TryParse(ctx.Request.Query["page"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPage)
        && parsedPage > 0)
    {
        page = parsedPage;
    }

    var perPage = defaultPerPage;
    if (int.TryParse(ctx.Request.Query["per_page"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPerPage)
        && parsedPerPage > 0)
    {
        perPage = parsedPerPage;
    }

    return (page, perPage);
}

/// <summary>Slices <paramref name="all"/> to the requested page and, when more rows remain, adds the
/// RFC 8288 `Link: rel="next"` header the connector's own `LinkHeaderPaginator` follows -- the last
/// page carries no such header, which is what stops the connector's page walk.</summary>
static IResult PagedJson<T>(HttpContext ctx, IReadOnlyList<T> all, int page, int perPage)
{
    var skip = (page - 1) * perPage;
    var slice = all.Skip(skip).Take(perPage).ToList();
    var hasNext = skip + slice.Count < all.Count;
    if (hasNext)
    {
        ctx.Response.Headers.Append("Link", BuildNextLink(ctx, page + 1));
    }

    return Results.Json(slice);
}

static string BuildNextLink(HttpContext ctx, int nextPage)
{
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.Path}";
    var query = QueryHelpers.ParseQuery(ctx.Request.QueryString.Value ?? "");
    var next = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var pair in query)
    {
        next[pair.Key] = pair.Value.ToString();
    }

    next["page"] = nextPage.ToString(CultureInfo.InvariantCulture);
    var url = QueryHelpers.AddQueryString(baseUrl, next);
    return $"<{url}>; rel=\"next\"";
}

static Task WriteForcedStatusAsync(HttpContext context, int status) => status switch
{
    429 => Forced429Async(context),
    403 => Forced403Async(context),
    401 => ForcedJsonAsync(context, 401, "Bad credentials"),
    404 => ForcedJsonAsync(context, 404, "Not Found"),
    422 => ForcedJsonAsync(context, 422, "Validation Failed"),
    _ => ForcedJsonAsync(context, status, $"forced status {status}"),
};

static async Task Forced429Async(HttpContext context)
{
    context.Response.Headers["Retry-After"] = "5";
    context.Response.StatusCode = 429;
    await context.Response.WriteAsJsonAsync(new { message = "You have exceeded a rate limit" });
}

static async Task Forced403Async(HttpContext context)
{
    var remaining = context.Request.Query["remaining"].FirstOrDefault() ?? "0";
    context.Response.Headers["x-ratelimit-remaining"] = remaining;
    context.Response.Headers["x-ratelimit-reset"] =
        DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    context.Response.StatusCode = 403;
    await context.Response.WriteAsJsonAsync(new { message = "API rate limit exceeded" });
}

static async Task ForcedJsonAsync(HttpContext context, int status, string message)
{
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { message });
}

/// <summary>Makes the implicit top-level-statements `Program` class public so
/// `WebApplicationFactory&lt;Program&gt;` in another assembly can reference it.</summary>
public partial class Program;
