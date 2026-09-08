using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Apache.Arrow;
using Pz.Connectors.Abstractions;
using Pz.Connectors.Abstractions.Batches;

namespace Pz.Connector.Github;

/// <summary>Reads one GitHub entity kind to completion: builds the first request URL from
/// <paramref name="config"/>/<paramref name="spec"/>'s watermark, follows
/// <see cref="LinkHeaderPaginator.NextUrl"/> page by page, and stops early -- without requesting a
/// further page -- once a page's rows fall at or before the watermark, for the two kinds
/// (pulls, releases) whose list endpoint accepts no server-side `since`-style filter. Every row goes
/// through <see cref="ArrowBatchBuilder"/>; this type never touches an Arrow array builder directly.</summary>
internal sealed class GithubPartition(
    HttpClient client, GithubDatasetConfig config, string? resolvedRef, DatasetSpec spec,
    GithubRedactor redactor, TimeProvider timeProvider) : IDatasetPartition
{
    private readonly DateTimeOffset? _watermark = ParseWatermark(spec.WatermarkValue);

    public async IAsyncEnumerable<RecordBatch> ReadAsync(BatchOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var watermarkIso = _watermark?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var schema = GithubSchemaSelector.For(config.Entity.Kind);
        var builder = new ArrowBatchBuilder(schema, options.TargetBatchBytes, maxRowsPerBatch: options.MaxRowsPerBatch);
        var url = BuildInitialUrl(watermarkIso);
        var stop = false;

        while (url is not null && !stop)
        {
            ct.ThrowIfCancellationRequested();
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await GithubErrors.FromResponseAsync(response, redactor, timeProvider,
                    $"reading {config.Entity.Owner}/{config.Entity.Repo}/{GithubDatasetConfig.KindName(config.Entity.Kind)}")
                    .ConfigureAwait(false);
            }

            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            switch (config.Entity.Kind)
            {
                case GithubEntityKind.Issues:
                {
                    var items = await JsonSerializer.DeserializeAsync(body, GithubJsonContext.Default.IssueDtoArray, ct).ConfigureAwait(false);
                    foreach (var item in items ?? [])
                    {
                        if (!IssuesSchema.TryToRow(item, out var row))
                        {
                            continue; // pull-request-shaped item: skip, never a stop
                        }

                        builder.AppendRow(row!);
                        if (builder.TryTakeBatch(out var batch))
                        {
                            yield return batch!;
                        }
                    }

                    break;
                }

                case GithubEntityKind.Pulls:
                {
                    var items = await JsonSerializer.DeserializeAsync(body, GithubJsonContext.Default.PullDtoArray, ct).ConfigureAwait(false);
                    var sawAtOrBefore = false;
                    foreach (var item in items ?? [])
                    {
                        if (IsAtOrBefore(item.UpdatedAt, _watermark))
                        {
                            sawAtOrBefore = true;
                            continue;
                        }

                        builder.AppendRow(PullsSchema.ToRow(item));
                        if (builder.TryTakeBatch(out var batch))
                        {
                            yield return batch!;
                        }
                    }

                    if (sawAtOrBefore)
                    {
                        stop = true;
                    }

                    break;
                }

                case GithubEntityKind.IssueComments:
                {
                    var items = await JsonSerializer.DeserializeAsync(body, GithubJsonContext.Default.IssueCommentDtoArray, ct).ConfigureAwait(false);
                    foreach (var item in items ?? [])
                    {
                        builder.AppendRow(IssueCommentsSchema.ToRow(item));
                        if (builder.TryTakeBatch(out var batch))
                        {
                            yield return batch!;
                        }
                    }

                    break;
                }

                case GithubEntityKind.Commits:
                {
                    var items = await JsonSerializer.DeserializeAsync(body, GithubJsonContext.Default.CommitDtoArray, ct).ConfigureAwait(false);
                    foreach (var item in items ?? [])
                    {
                        builder.AppendRow(CommitsSchema.ToRow(item));
                        if (builder.TryTakeBatch(out var batch))
                        {
                            yield return batch!;
                        }
                    }

                    break;
                }

                case GithubEntityKind.Releases:
                {
                    var items = await JsonSerializer.DeserializeAsync(body, GithubJsonContext.Default.ReleaseDtoArray, ct).ConfigureAwait(false);
                    var sawAtOrBefore = false;
                    foreach (var item in items ?? [])
                    {
                        if (IsAtOrBefore(item.CreatedAt, _watermark))
                        {
                            sawAtOrBefore = true;
                            continue;
                        }

                        builder.AppendRow(ReleasesSchema.ToRow(item));
                        if (builder.TryTakeBatch(out var batch))
                        {
                            yield return batch!;
                        }
                    }

                    if (sawAtOrBefore)
                    {
                        stop = true;
                    }

                    break;
                }

                case GithubEntityKind.ActionsRuns:
                {
                    var items = await JsonSerializer.DeserializeAsync(body, GithubJsonContext.Default.ActionsRunDtoArray, ct).ConfigureAwait(false);
                    foreach (var item in items ?? [])
                    {
                        builder.AppendRow(ActionsRunsSchema.ToRow(item));
                        if (builder.TryTakeBatch(out var batch))
                        {
                            yield return batch!;
                        }
                    }

                    break;
                }

                default:
                    throw new NotSupportedException($"unknown entity kind {config.Entity.Kind}");
            }

            url = stop ? null : LinkHeaderPaginator.NextUrl(response);
        }

        var last = builder.Flush();
        if (last is not null)
        {
            yield return last;
        }
    }

    private string BuildInitialUrl(string? watermarkIso)
    {
        var owner = config.Entity.Owner;
        var repo = config.Entity.Repo;
        var perPage = config.PerPage;

        return config.Entity.Kind switch
        {
            GithubEntityKind.Issues =>
                $"/repos/{owner}/{repo}/issues?state=all&sort=updated&direction=asc&per_page={perPage}"
                + (watermarkIso is null ? "" : $"&since={watermarkIso}"),
            GithubEntityKind.Pulls =>
                $"/repos/{owner}/{repo}/pulls?state=all&sort=updated&direction=desc&per_page={perPage}",
            GithubEntityKind.IssueComments =>
                $"/repos/{owner}/{repo}/issues/comments?sort=updated&direction=asc&per_page={perPage}"
                + (watermarkIso is null ? "" : $"&since={watermarkIso}"),
            GithubEntityKind.Commits =>
                $"/repos/{owner}/{repo}/commits?sha={Uri.EscapeDataString(resolvedRef!)}&per_page={perPage}"
                + (watermarkIso is null ? "" : $"&since={watermarkIso}"),
            GithubEntityKind.Releases =>
                $"/repos/{owner}/{repo}/releases?per_page={perPage}",
            GithubEntityKind.ActionsRuns =>
                $"/repos/{owner}/{repo}/actions/runs?per_page={perPage}"
                + (watermarkIso is null ? "" : $"&created={Uri.EscapeDataString(">=" + watermarkIso)}"),
            _ => throw new NotSupportedException($"unknown entity kind {config.Entity.Kind}"),
        };
    }

    /// <summary>Compares one row's timestamp column against the watermark for the client-side early
    /// stop (pulls/releases, on `updated_at`/`created_at` respectively). A missing watermark or an
    /// unparseable item timestamp is never "at or before" -- the row is kept rather than silently
    /// dropped.</summary>
    private static bool IsAtOrBefore(string? itemTimestamp, DateTimeOffset? watermark)
    {
        if (watermark is null)
        {
            return false;
        }

        var parsed = GithubTimestamps.Parse(itemTimestamp);
        return parsed is not null && parsed <= watermark;
    }

    private static DateTimeOffset? ParseWatermark(string? value) => value is null
        ? null
        : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
