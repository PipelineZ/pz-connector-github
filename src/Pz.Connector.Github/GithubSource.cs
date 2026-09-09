using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github;

/// <summary>Opens one GitHub REST API connection into a readable source. Every dataset maps to
/// exactly one pz partition (<see cref="PlanReadAsync"/>) -- pagination happens inside that
/// partition's own read loop, not across separate pz partitions. There is no SQL fragment DuckDB
/// could scan a REST API with, so <see cref="TryGetNativeScan"/> always declines.</summary>
internal sealed class GithubSource(GithubConnectionConfig connection, HttpClient client, ILogger logger,
    TimeProvider? timeProvider = null) : ISource
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ValueTask<DatasetSchema> GetSchemaAsync(DatasetSpec spec, CancellationToken ct)
    {
        var dataset = ParseDataset(spec);
        return ValueTask.FromResult(new DatasetSchema(GithubSchemaSelector.For(dataset.Entity.Kind)));
    }

    public bool TryGetNativeScan(DatasetSpec spec, [NotNullWhen(true)] out NativeScan? scan)
    {
        scan = null;
        return false;
    }

    public async ValueTask<IReadOnlyList<IDatasetPartition>> PlanReadAsync(DatasetSpec spec, ReadHints hints, CancellationToken ct)
    {
        var dataset = ParseDataset(spec);
        var resolvedRef = dataset.Ref;
        if (dataset.Entity.Kind == GithubEntityKind.Commits && resolvedRef is null)
        {
            resolvedRef = await ResolveDefaultBranchAsync(dataset.Entity.Owner, dataset.Entity.Repo, ct).ConfigureAwait(false);
        }

        logger.LogDebug("github: dataset {Dataset}: planned one partition for {Owner}/{Repo}/{Kind}",
            spec.Dataset, dataset.Entity.Owner, dataset.Entity.Repo, GithubDatasetConfig.KindName(dataset.Entity.Kind));

        IReadOnlyList<IDatasetPartition> partitions =
            [new GithubPartition(client, dataset, resolvedRef, spec, connection.Redactor, _timeProvider)];
        return partitions;
    }

    public ValueTask DisposeAsync()
    {
        // GithubSource owns the HttpClient it was constructed with (built in
        // GithubConnector.OpenAsync solely for this source) -- nothing else can dispose it.
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private GithubDatasetConfig ParseDataset(DatasetSpec spec)
    {
        var errors = new List<string>();
        return GithubDatasetConfig.Parse(spec, errors)
            ?? throw new PzConnectorException($"github: {string.Join("; ", errors)}", isTransient: false);
    }

    /// <summary>Resolves a commits dataset's `ref:` when none was configured: GitHub's commits-list
    /// endpoint requires an explicit `sha`/branch, and the repo's default branch is the only sensible
    /// implicit choice. One request per <see cref="PlanReadAsync"/> call -- no cross-call caching, since
    /// nothing in the spec asks for one and a stale cached branch would be a correctness bug, not an
    /// optimization.</summary>
    private async Task<string?> ResolveDefaultBranchAsync(string owner, string repo, CancellationToken ct)
    {
        var context = $"resolving default branch for {owner}/{repo}";

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync($"repos/{owner}/{repo}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the engine's own cancellation -- never wrapped
        }
        catch (Exception ex)
        {
            throw GithubErrors.Wrap(ex, connection.Redactor, context);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await GithubErrors.FromResponseAsync(response, connection.Redactor, _timeProvider, context)
                    .ConfigureAwait(false);
            }

            try
            {
                var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var repoDto = await JsonSerializer.DeserializeAsync(body, GithubJsonContext.Default.RepoDto, ct)
                    .ConfigureAwait(false);
                return repoDto?.DefaultBranch;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw GithubErrors.Wrap(ex, connection.Redactor, context);
            }
        }
    }
}
