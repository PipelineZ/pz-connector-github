using System.Globalization;
using System.Text.Json.Serialization;
using Apache.Arrow;
using Apache.Arrow.Types;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github;

/// <summary>One item off GitHub's issue-comments endpoint. That endpoint serves comments for both
/// issues and pull requests, always under the `/issues/{n}/comments` path -- there is no separate
/// `/pulls/{n}/comments` shape to model.</summary>
internal sealed record IssueCommentDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("issue_url")] string? IssueUrl,
    [property: JsonPropertyName("user")] GithubUserDto? User,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt,
    [property: JsonPropertyName("html_url")] string? HtmlUrl);

/// <summary>Arrow shape for the `issues/comments` entity (spec §6.3) and the pure DTO-to-row mapping
/// `GithubPartition` (Task 7) calls per page item. Every column is nullable.</summary>
internal static class IssueCommentsSchema
{
    public static readonly Schema Schema = new(
    [
        new Field("id", Int64Type.Default, nullable: true),
        new Field("issue_number", Int32Type.Default, nullable: true),
        new Field("author", StringType.Default, nullable: true),
        new Field("body", StringType.Default, nullable: true),
        new Field("created_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("updated_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("html_url", StringType.Default, nullable: true),
    ], null);

    /// <summary>Maps one issue-comments-list item to a row in <see cref="Schema"/>'s field order.
    /// Throws (via <see cref="ParseIssueNumber"/>) when `issue_url` is missing or does not end in a
    /// parseable integer segment.</summary>
    public static object?[] ToRow(IssueCommentDto dto) =>
    [
        dto.Id,
        ParseIssueNumber(dto.Id, dto.IssueUrl),
        dto.User?.Login,
        dto.Body,
        GithubTimestamps.Parse(dto.CreatedAt),
        GithubTimestamps.Parse(dto.UpdatedAt),
        dto.HtmlUrl,
    ];

    /// <summary>Extracts the issue/PR number from the trailing path segment of `issue_url`
    /// (`".../issues/42"` -&gt; `42`). GitHub's comments endpoint always uses `/issues/{n}` for both
    /// issue and PR comments, never `/pulls/{n}`, but this just takes whatever the last `/`-separated
    /// segment is and parses it as an integer, so it stays correct either way. <paramref name="id"/>
    /// is the comment's own id, folded into the error message on failure since the caller has no
    /// other way to name the offending row.</summary>
    internal static int ParseIssueNumber(long id, string? issueUrl)
    {
        var segment = issueUrl?.Split('/').LastOrDefault();
        if (segment is not null
            && int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        throw new PzConnectorException(
            $"github: issue comment '{id}': cannot parse issue number from issue_url '{issueUrl}'",
            isTransient: false);
    }
}
