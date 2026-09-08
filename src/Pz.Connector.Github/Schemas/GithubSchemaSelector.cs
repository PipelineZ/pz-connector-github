using Apache.Arrow;

namespace Pz.Connector.Github;

/// <summary>Chooses the per-entity-kind Arrow schema. Shared by <c>GithubSource.GetSchemaAsync</c>
/// and <c>GithubPartition.ReadAsync</c> so the two can never drift out of sync with each other.</summary>
internal static class GithubSchemaSelector
{
    public static Schema For(GithubEntityKind kind) => kind switch
    {
        GithubEntityKind.Issues => IssuesSchema.Schema,
        GithubEntityKind.Pulls => PullsSchema.Schema,
        GithubEntityKind.IssueComments => IssueCommentsSchema.Schema,
        GithubEntityKind.Commits => CommitsSchema.Schema,
        GithubEntityKind.Releases => ReleasesSchema.Schema,
        GithubEntityKind.ActionsRuns => ActionsRunsSchema.Schema,
        _ => throw new NotSupportedException($"unknown entity kind {kind}"),
    };
}
