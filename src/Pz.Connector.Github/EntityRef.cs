using System.Text.RegularExpressions;

namespace Pz.Connector.Github;

/// <summary>Parses pz's <c>owner/repo/kind</c> entity addressing into a typed reference. The kind
/// segment can itself contain a slash (<c>issues/comments</c>, <c>actions/runs</c>), so parsing
/// takes the first two segments as owner/repo and rejoins everything after them before matching --
/// never splits the kind on a fixed segment count.</summary>
public readonly partial record struct EntityRef(string Owner, string Repo, GithubEntityKind Kind)
{
    // Not a security boundary (the request host always comes from BaseAddress, never owner/repo) --
    // this guards against the same silent-wrong-request class as an unescaped `ref`: a typo'd '?',
    // '#', or space in an authored `entity:` value would otherwise become a different, confusingly
    // failing request instead of a clear compile-time error. Roughly GitHub's own real owner/repo
    // charset.
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex ValidNameSegment();

    public static bool TryParse(string entity, out EntityRef result)
    {
        var segments = entity.Split('/');
        if (segments.Length < 3)
        {
            result = default;
            return false;
        }

        if (!ValidNameSegment().IsMatch(segments[0]) || !ValidNameSegment().IsMatch(segments[1]))
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
