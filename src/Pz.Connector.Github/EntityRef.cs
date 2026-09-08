namespace Pz.Connector.Github;

/// <summary>Parses pz's <c>owner/repo/kind</c> entity addressing into a typed reference. The kind
/// segment can itself contain a slash (<c>issues/comments</c>, <c>actions/runs</c>), so parsing
/// takes the first two segments as owner/repo and rejoins everything after them before matching --
/// never splits the kind on a fixed segment count.</summary>
public readonly record struct EntityRef(string Owner, string Repo, GithubEntityKind Kind)
{
    public static bool TryParse(string entity, out EntityRef result)
    {
        var segments = entity.Split('/');
        if (segments.Length < 3)
        {
            result = default;
            return false;
        }

        var kindString = string.Join('/', segments[2..]);
        GithubEntityKind? kind = kindString switch
        {
            "issues" => GithubEntityKind.Issues,
            "pulls" => GithubEntityKind.Pulls,
            "issues/comments" => GithubEntityKind.IssueComments,
            "commits" => GithubEntityKind.Commits,
            "releases" => GithubEntityKind.Releases,
            "actions/runs" => GithubEntityKind.ActionsRuns,
            _ => null,
        };

        if (kind is null)
        {
            result = default;
            return false;
        }

        result = new EntityRef(segments[0], segments[1], kind.Value);
        return true;
    }
}
