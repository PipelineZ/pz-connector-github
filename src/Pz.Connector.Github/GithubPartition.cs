using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
    private readonly DateTimeOffset? _watermark = ParseWatermark(spec, config.Entity.Kind, redactor);

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
            using var response = await GetPageAsync(url, ct).ConfigureAwait(false);

            switch (config.Entity.Kind)
            {
                case GithubEntityKind.Issues:
                {
                    var items = await DeserializePageAsync(response, GithubJsonContext.Default.IssueDtoArray, ct).ConfigureAwait(false);
                    foreach (var item in items)
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
                    var items = await DeserializePageAsync(response, GithubJsonContext.Default.PullDtoArray, ct).ConfigureAwait(false);
                    var sawAtOrBefore = false;
                    foreach (var item in items)
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
                    var items = await DeserializePageAsync(response, GithubJsonContext.Default.IssueCommentDtoArray, ct).ConfigureAwait(false);
                    foreach (var item in items)
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
                    var items = await DeserializePageAsync(response, GithubJsonContext.Default.CommitDtoArray, ct).ConfigureAwait(false);
                    foreach (var item in items)
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
                    var items = await DeserializePageAsync(response, GithubJsonContext.Default.ReleaseDtoArray, ct).ConfigureAwait(false);
                    var sawAtOrBefore = false;
                    foreach (var item in items)
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
                    var items = await DeserializePageAsync(response, GithubJsonContext.Default.ActionsRunDtoArray, ct).ConfigureAwait(false);
                    foreach (var item in items)
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
                $"repos/{owner}/{repo}/issues?state=all&sort=updated&direction=asc&per_page={perPage}"
                + (watermarkIso is null ? "" : $"&since={watermarkIso}"),
            GithubEntityKind.Pulls =>
                $"repos/{owner}/{repo}/pulls?state=all&sort=updated&direction=desc&per_page={perPage}",
            GithubEntityKind.IssueComments =>
                $"repos/{owner}/{repo}/issues/comments?sort=updated&direction=asc&per_page={perPage}"
                + (watermarkIso is null ? "" : $"&since={watermarkIso}"),
            GithubEntityKind.Commits =>
                $"repos/{owner}/{repo}/commits?sha={Uri.EscapeDataString(resolvedRef!)}&per_page={perPage}"
                + (watermarkIso is null ? "" : $"&since={watermarkIso}"),
            GithubEntityKind.Releases =>
                $"repos/{owner}/{repo}/releases?per_page={perPage}",
            GithubEntityKind.ActionsRuns =>
                $"repos/{owner}/{repo}/actions/runs?per_page={perPage}"
                + (watermarkIso is null ? "" : $"&created={Uri.EscapeDataString(">=" + watermarkIso)}"),
            _ => throw new NotSupportedException($"unknown entity kind {config.Entity.Kind}"),
        };
    }

    /// <summary>Issues the page request, classifying every failure the same way as every other GitHub
    /// call in this connector: a non-success response becomes the classified exception from
    /// <see cref="GithubErrors.FromResponseAsync"/>; a transport failure (no HTTP answer at all) is
    /// wrapped via <see cref="GithubErrors.Wrap"/> so the engine can tell it apart from a genuine
    /// application error. The caller's own cancellation is never wrapped.</summary>
    private async Task<HttpResponseMessage> GetPageAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw GithubErrors.Wrap(ex, redactor, Context());
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw await GithubErrors.FromResponseAsync(response, redactor, timeProvider, Context())
                    .ConfigureAwait(false);
            }
        }

        return response;
    }

    /// <summary>Reads and deserializes one page's body, classifying a truncated/malformed body the
    /// same way as a transport failure -- both mean the caller got no usable rows for this page.</summary>
    private async Task<T[]> DeserializePageAsync<T>(HttpResponseMessage response, JsonTypeInfo<T[]> typeInfo, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(body, typeInfo, ct).ConfigureAwait(false) ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw GithubErrors.Wrap(ex, redactor, Context());
        }
    }

    private string Context() =>
        $"reading {config.Entity.Owner}/{config.Entity.Repo}/{GithubDatasetConfig.KindName(config.Entity.Kind)}";

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

    /// <summary>Validates the pipeline SQL's declared watermark cursor against this kind's actual
    /// cursor column (a mismatch is a clear, non-transient error rather than silent wrong behavior --
    /// this connector always filters on its own hardcoded per-kind column, so a mismatched
    /// <see cref="DatasetSpec.WatermarkCursor"/> would otherwise be silently ignored) and parses the
    /// watermark value. The value arrives in whatever canonical form the pipeline's declared cursor
    /// type produces (e.g. a bare integer like <c>"42"</c> for `where number > {{ watermark(s, e) }}`
    /// on an issues dataset's `number` column) -- not necessarily a timestamp, since this connector's
    /// watermark support is SQL-declared, not `columns:`-contracted. A value that isn't a valid
    /// timestamp is refused with the same clear-error shape as a cursor mismatch, never a raw
    /// unclassified <see cref="FormatException"/>.</summary>
    private static DateTimeOffset? ParseWatermark(DatasetSpec spec, GithubEntityKind kind, GithubRedactor redactor)
    {
        var cursorColumn = WatermarkColumn(kind);

        if (spec.WatermarkCursor is not null
            && !string.Equals(spec.WatermarkCursor, cursorColumn, StringComparison.Ordinal))
        {
            throw new PzConnectorException(redactor.Redact(
                $"github: dataset '{spec.Dataset}': watermark cursor '{spec.WatermarkCursor}' does not match " +
                $"the '{GithubDatasetConfig.KindName(kind)}' entity's cursor column '{cursorColumn}'"),
                isTransient: false);
        }

        if (spec.WatermarkValue is null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(spec.WatermarkValue, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            throw new PzConnectorException(redactor.Redact(
                $"github: dataset '{spec.Dataset}': watermark value '{spec.WatermarkValue}' is not a valid " +
                $"timestamp for cursor '{cursorColumn}'"),
                isTransient: false);
        }

        return parsed;
    }

    /// <summary>The column this kind's watermark is actually applied against -- see the per-kind
    /// tables in the README/design spec §6. Kept alongside <see cref="ParseWatermark"/> rather than
    /// in <c>GithubDatasetConfig</c>: it describes read-loop behavior, not a config shape.</summary>
    private static string WatermarkColumn(GithubEntityKind kind) => kind switch
    {
        GithubEntityKind.Issues => "updated_at",
        GithubEntityKind.Pulls => "updated_at",
        GithubEntityKind.IssueComments => "updated_at",
        GithubEntityKind.Commits => "committed_at",
        GithubEntityKind.Releases => "created_at",
        GithubEntityKind.ActionsRuns => "updated_at",
        _ => throw new NotSupportedException($"unknown entity kind {kind}"),
    };
}
