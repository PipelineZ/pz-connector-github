namespace Pz.Connector.Github;

/// <summary>The GitHub REST resources this connector can address, matched from the entity's
/// path segments beyond owner/repo. See <see cref="EntityRef.TryParse"/>.</summary>
public enum GithubEntityKind
{
    Issues,
    Pulls,
    IssueComments,
    Commits,
    Releases,
    ActionsRuns,
}
