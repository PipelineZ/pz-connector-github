using System.Globalization;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github;

/// <summary>Per-dataset read options: <c>entity</c> (defaults to the pz dataset name), <c>per_page</c>
/// (1..100, default 100), <c>ref</c> (accepted only for a <see cref="GithubEntityKind.Commits"/>
/// entity). Follows <c>GithubConnectionConfig</c>'s parse-and-aggregate discipline -- every field is
/// validated before returning, and a single <see cref="List{T}"/> collects every error rather than
/// failing fast.</summary>
internal sealed record GithubDatasetConfig(EntityRef Entity, int PerPage, string? Ref)
{
    private static readonly string[] KnownKeys = ["entity", "per_page", "ref"];

    public static GithubDatasetConfig? Parse(DatasetSpec spec, List<string> errors)
    {
        var start = errors.Count;

        foreach (var key in spec.Options.Keys.Where(k => !KnownKeys.Contains(k, StringComparer.Ordinal)))
        {
            errors.Add($"dataset '{spec.Dataset}': unknown read option '{key}'; known: entity, per_page, ref");
        }

        var entityValue = spec.Dataset;
        if (spec.Options.TryGetValue("entity", out var entityRaw) && entityRaw is not null)
        {
            entityValue = entityRaw.ToString() ?? "";
        }

        var entityOk = EntityRef.TryParse(entityValue, out var entity);
        if (!entityOk)
        {
            errors.Add($"dataset '{spec.Dataset}': 'entity' value '{entityValue}' is not a valid GitHub " +
                "entity reference (expected '{owner}/{repo}/{kind}' where kind is one of: issues, pulls, " +
                "issues/comments, commits, releases, actions/runs)");
        }

        var perPage = 100;
        if (spec.Options.TryGetValue("per_page", out var perPageRaw) && perPageRaw is not null)
        {
            int parsed;
            // Convert.ToInt32(bool) silently succeeds (true -> 1, false -> 0) -- exactly the kind of
            // silent-wrong-value a typo'd `per_page: true` must not produce. Reject it up front with
            // the same error shape as any other non-integer value, rather than letting the request
            // quietly ask for 1 row per page.
            if (perPageRaw is bool)
            {
                errors.Add($"dataset '{spec.Dataset}': 'per_page' must be an integer; got '{perPageRaw}'");
                parsed = 100;
            }
            else
            {
                try
                {
                    parsed = Convert.ToInt32(perPageRaw, CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
                {
                    errors.Add($"dataset '{spec.Dataset}': 'per_page' must be an integer; got '{perPageRaw}'");
                    parsed = 100;
                }
            }

            if (parsed is < 1 or > 100)
            {
                errors.Add($"dataset '{spec.Dataset}': 'per_page' must be between 1 and 100; got {parsed}");
            }
            else
            {
                perPage = parsed;
            }
        }

        string? refValue = null;
        if (entityOk && spec.Options.TryGetValue("ref", out var refRaw) && refRaw is not null)
        {
            if (entity.Kind != GithubEntityKind.Commits)
            {
                errors.Add($"dataset '{spec.Dataset}': 'ref' is only valid for commits entities, not {KindName(entity.Kind)}");
            }
            else
            {
                refValue = refRaw.ToString() ?? "";
            }
        }

        return errors.Count == start ? new GithubDatasetConfig(entity, perPage, refValue) : null;
    }

    /// <summary>The entity-reference kind string for <paramref name="kind"/>, matching the segment
    /// <see cref="EntityRef.TryParse"/> accepts (not <see cref="Enum.ToString()"/>, which would print
    /// the PascalCase member name instead). Internal (not private) so <c>GithubPartition</c> can reuse
    /// it verbatim for its own error-context strings rather than duplicating the switch.</summary>
    internal static string KindName(GithubEntityKind kind) => kind switch
    {
        GithubEntityKind.Issues => "issues",
        GithubEntityKind.Pulls => "pulls",
        GithubEntityKind.IssueComments => "issues/comments",
        GithubEntityKind.Commits => "commits",
        GithubEntityKind.Releases => "releases",
        GithubEntityKind.ActionsRuns => "actions/runs",
        _ => kind.ToString(),
    };
}
