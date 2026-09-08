# Pz.Connector.Github

GitHub source for [PipelineZ](https://pipelinez.dev) (`pz`), served out of process. One connection
is one GitHub host (github.com or a GHES instance); a dataset is one repository's issues, pull
requests, issue/PR comments, commits, releases, or Actions workflow runs, addressed the way GitHub
itself addresses them. Source only -- there is no write side (see Non-goals in the design spec).

## Installation

```yaml
# project.yml
connectors:
  - package: Pz.Connector.Github
    version: 0.1.0
```

`pz restore` installs the Native AOT binary for your platform (linux-x64, linux-arm64, osx-arm64,
win-x64) and `pz run` spawns it. Needs pz 0.6.0 or newer. Talks to the REST API via a raw HTTP
client, not Octokit.

| RID | status |
|---|---|
| `linux-x64` | the platform every test and the packaging proof run on |
| `linux-arm64`, `osx-arm64`, `win-x64` | shipped, never exercised by this connector's own CI |

No self-contained fallback: every RID above ships Native AOT only.

## Connection

```yaml
# connections.yml
gh:
  connector: github
  url: https://api.github.com     # optional; default shown. GHES: https://ghe.example.com/api/v3
  token: ${GITHUB_TOKEN}          # optional; omit for unauthenticated public-repo reads (60 req/hr)
```

`url`, if present, must be an absolute `http`/`https` URI with no trailing slash (normalized if one
is given). `token`, if present, must be non-empty. Unknown keys are refused, naming the known ones.

Every request carries `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28`,
`User-Agent: pz-connector-github/<version>`, and (when configured) `Authorization: Bearer <token>`.
`pz connector check` runs `GET /rate_limit` (and, when a token is set, `GET /user`) and reports
`authenticated as <login>, <remaining>/<limit> requests remaining` or the unauthenticated
equivalent. `token` is redacted from every error, including `Authorization: Bearer …` header echoes.

## Entity addressing

An entity name is `{owner}/{repo}/{kind}`, split on `/`: the first two segments are `owner` and
`repo`; every remaining segment, rejoined with `/`, is matched case-sensitively against six kind
strings (`issues/comments` and `actions/runs` are themselves two segments). Any other kind string is
refused at compile time.

| kind | what it reads |
|---|---|
| `issues` | the repo's issues (pull requests are filtered out of this feed; see `pulls`) |
| `pulls` | the repo's pull requests |
| `issues/comments` | comments on any issue or pull request in the repo |
| `commits` | commit history of one ref (default: the repo's default branch) |
| `releases` | the repo's releases |
| `actions/runs` | Actions workflow runs |

```yaml
  entities:
    open_issues:
      read:
        entity: PipelineZ/pz/issues   # optional; defaults to the pz entity name itself
        per_page: 100                 # optional; 1..100 (GitHub's own cap), default 100
    pz_commits:
      read:
        entity: PipelineZ/pz/commits
        ref: main                     # commits only; optional, defaults to the repo's default branch
```

`entity:` lets the pz entity name differ from the GitHub path. `ref:` is only valid on a `commits`
entity. No column pruning or predicate pushdown beyond the watermark: schemas are fixed and small.
GitHub's REST filters (`since`, `created`) are driven internally from the watermark; `state` is
always `all` (fixed, not a read option) so both open and closed items are always read. There is no
`query:` escape hatch and no `state:` read option.

## Schemas

Every column is nullable String/Int32/Int64/Boolean/Timestamp(µs, UTC) unless noted; GitHub's
`created_at`/`updated_at`/etc. are always `Z`-suffixed ISO-8601 UTC. A field GitHub can return `null`
(e.g. `merged_at` on an open PR, `closed_at` on an open issue) is null in that row.

### `issues`

| column | source | type |
|---|---|---|
| `id` | `id` | Int64 |
| `number` | `number` | Int32 |
| `title` | `title` | String |
| `state` | `state` (`open`/`closed`) | String |
| `author` | `user.login` | String |
| `body` | `body` | String |
| `labels` | `labels[].name`, JSON array text | String (JSON) |
| `assignees` | `assignees[].login`, JSON array text | String (JSON) |
| `milestone` | `milestone.title` | String |
| `comments` | `comments` | Int32 |
| `created_at` / `updated_at` / `closed_at` | same | Timestamp |
| `html_url` | `html_url` | String |

The issues endpoint also returns pull requests; any item carrying a `pull_request` field is skipped
(it belongs to `pulls`).

### `pulls`

| column | source | type |
|---|---|---|
| `id` | `id` | Int64 |
| `number` | `number` | Int32 |
| `title` | `title` | String |
| `state` | `state` | String |
| `author` | `user.login` | String |
| `body` | `body` | String |
| `draft` | `draft` | Boolean |
| `base_ref` / `head_ref` | `base.ref` / `head.ref` | String |
| `merged_at` | `merged_at` | Timestamp |
| `created_at` / `updated_at` / `closed_at` | same | Timestamp |
| `html_url` | `html_url` | String |

Diff stats (`additions`/`deletions`/`changed_files`) are not on the list response, only the
single-PR `GET`; fetching them per row would be an N+1 call per PR and is out of scope for v1.

### `issues/comments`

| column | source | type |
|---|---|---|
| `id` | `id` | Int64 |
| `issue_number` | parsed from the trailing path segment of `issue_url` | Int32 |
| `author` | `user.login` | String |
| `body` | `body` | String |
| `created_at` / `updated_at` | same | Timestamp |
| `html_url` | `html_url` | String |

### `commits`

Always in the topological history of the configured `ref` (default: the repo's default branch,
resolved once via `GET /repos/{o}/{r}` at plan-read time, i.e. inside `PlanReadAsync`, not
`GetSchemaAsync`).

| column | source | type |
|---|---|---|
| `sha` | `sha` | String |
| `author_name` / `author_email` | `commit.author.name` / `.email` | String |
| `author_login` | `author.login` (the GitHub account, if the commit email matches one; else null) | String |
| `committed_at` | `commit.committer.date` | Timestamp |
| `message` | `commit.message` | String |
| `html_url` | `html_url` | String |

### `releases`

| column | source | type |
|---|---|---|
| `id` | `id` | Int64 |
| `tag_name` | `tag_name` | String |
| `name` | `name` | String |
| `body` | `body` | String |
| `draft` / `prerelease` | same | Boolean |
| `author` | `author.login` | String |
| `created_at` / `published_at` | same | Timestamp |
| `html_url` | `html_url` | String |

### `actions/runs`

| column | source | type |
|---|---|---|
| `id` | `id` | Int64 |
| `name` | `name` | String |
| `run_number` | `run_number` | Int32 |
| `head_branch` | `head_branch` | String |
| `head_sha` | `head_sha` | String |
| `event` | `event` | String |
| `status` | `status` | String |
| `conclusion` | `conclusion` | String |
| `created_at` / `updated_at` / `run_started_at` | same | Timestamp |
| `html_url` | `html_url` | String |

## Watermarks

Every watermark below is a lower bound only (`where <col> > {{ watermark(s, e) }}`); no `BoundedWindow`
capability, no `ColumnPruning`.

| kind | watermark column | how it's applied |
|---|---|---|
| `issues` | `updated_at` | server-side `since` filter |
| `pulls` | `updated_at` | **no server-side filter** -- paged newest-updated-first, stops requesting further pages once a page's last row falls at or before the watermark (rows at or before it are still dropped individually within that page) |
| `issues/comments` | `updated_at` | server-side `since` filter |
| `commits` | `committed_at` | server-side `since` filter against `commit.committer.date`; commits are immutable once landed, so this is a safe append-oriented cursor |
| `releases` | `created_at` | **no filter param at all** -- paged newest-`created_at`-first, same early-stop as `pulls` |
| `actions/runs` | `updated_at` | server-side range filter (`created=>=<ts>`) narrows the scan; `updated_at` (not `created_at`) is the cursor because a run's `status`/`conclusion` keep changing after creation, and updated-in-place rows inside the window are re-emitted **as long as the run's `created_at` is still at or after the watermark floor** |

**Known limitation (`actions/runs`):** the `created>=<watermark>` server-side filter can permanently
miss a run's final state. A run created *before* the watermark, but which is still updating (e.g.
`status: in_progress`) when the watermark advances past its `created_at`, will not be re-read on a
later incremental run -- its final `status`/`conclusion` can go stale in the warehouse forever. Use
`pz run --full-refresh` periodically if you need guaranteed-fresh terminal states for older runs.
This is a known v1 trade-off, not a bug; a safety-window fix is tracked as a future improvement.

## Rate limiting

Every list request is paged via the standard `Link: <url>; rel="next"` response header (RFC 8288);
a partition follows `rel="next"` until it is absent or an early-stop rule (above) fires first.
`per_page` from the read config is passed on every request.

Every response's `x-ratelimit-remaining` / `x-ratelimit-reset` headers are read. `remaining == "0"`
becomes a transient failure with `RetryAfter` set from `reset_unix - now`, checked before even
looking at the status code (a `403` with a fresh reset always accompanies this, but the header pair
is unambiguous on its own). A `403` carrying a `Retry-After` header (GitHub's secondary rate limit,
independent of the primary counter) is transient with that `Retry-After` value. Neither case is
retried inside the connector -- pz's engine drives the retry from these signals.

## Errors

Every failure becomes a connector exception whose message is `github: <context>: …`, with `token`
redacted.

- No HTTP response (connection failure, a timeout not from pz's own cancellation) -- transient.
- `500`, `502`, `503`, `504` -- transient.
- Rate limit (above) -- transient, with `RetryAfter`.
- `401` -- non-transient, hint `check token`.
- `403` without rate-limit headers (e.g. token lacks a required scope) -- non-transient, hint `check
  token scopes (private repos need the repo scope)`.
- `404` -- non-transient, hint `check owner/repo and that the token can see it`.
- `422` (e.g. an unresolvable `ref:`) -- non-transient, GitHub's `message` field verbatim.
- Everything else -- non-transient, with the response body's `message` field, or the status line
  when the body isn't JSON.
- Cancellation propagates as `OperationCanceledException`, never wrapped.

## Development

```bash
dotnet build Pz.Connector.Github.slnx -c Release
dotnet test Pz.Connector.Github.slnx -c Release --no-build              # in-process fake server, no docker needed
dotnet restore src/Pz.Connector.Github -r linux-x64                     # once, on a cold cache
dotnet publish src/Pz.Connector.Github -c Release -r linux-x64 --no-restore
dotnet pack src/Pz.Connector.Github -c Release -o packages              # nupkg with pz.connector.json
```

`tests/fixtures/FakeGithubServer` is a standalone ASP.NET Core app that seeds one fixture repo
(`fixture/repo`, default branch `main`, 30 rows per kind) and requires
`Authorization: Bearer fixture-test-token` when an `Authorization` header is present at all (no
header is a valid unauthenticated call). The acceptance suite drives it in-process via
`WebApplicationFactory<Program>` -- no docker, no OS-specific test exclusions, every test runs on
both Linux and Windows CI runners. To point a real `pz` project at it for manual testing:

```bash
dotnet run --project tests/fixtures/FakeGithubServer --urls http://localhost:5099
```

then set `url: http://localhost:5099` and `token: fixture-test-token` (or omit `token:` entirely)
in `connections.yml`. `tests/e2e/` is the pz project CI runs against a packed nupkg and this fake
server. Releases are tag-triggered (`v*`) and publish to nuget.org through trusted publishing.
