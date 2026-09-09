using System.Text.Json;
using System.Text.Json.Serialization;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Pz.Connector.Github;

/// <summary>One item off GitHub's issues-list endpoint. That endpoint also returns pull requests
/// (they share the same underlying resource); an item that is actually a pull request carries a
/// non-null `pull_request` field, checked only for presence -- <see cref="JsonElement"/> stands in
/// for its shape so this DTO never has to model it.</summary>
internal sealed record IssueDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("user")] GithubUserDto? User,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("labels")] IReadOnlyList<LabelDto>? Labels,
    [property: JsonPropertyName("assignees")] IReadOnlyList<GithubUserDto>? Assignees,
    [property: JsonPropertyName("milestone")] MilestoneDto? Milestone,
    [property: JsonPropertyName("comments")] int Comments,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt,
    [property: JsonPropertyName("closed_at")] string? ClosedAt,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("pull_request")] JsonElement? PullRequest);

internal sealed record LabelDto([property: JsonPropertyName("name")] string? Name);

internal sealed record MilestoneDto([property: JsonPropertyName("title")] string? Title);

/// <summary>Arrow shape for the `issues` entity (spec §6.1) and the pure DTO-to-row mapping
/// `GithubPartition` (Task 7) calls per page item. Every column is nullable, including `id`/`number`
/// -- GitHub always sends them, but the schema itself makes no such promise.</summary>
internal static class IssuesSchema
{
    public static readonly Schema Schema = new(
    [
        new Field("id", Int64Type.Default, nullable: true),
        new Field("number", Int32Type.Default, nullable: true),
        new Field("title", StringType.Default, nullable: true),
        new Field("state", StringType.Default, nullable: true),
        new Field("author", StringType.Default, nullable: true),
        new Field("body", StringType.Default, nullable: true),
        new Field("labels", StringType.Default, nullable: true),
        new Field("assignees", StringType.Default, nullable: true),
        new Field("milestone", StringType.Default, nullable: true),
        new Field("comments", Int32Type.Default, nullable: true),
        new Field("created_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("updated_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("closed_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("html_url", StringType.Default, nullable: true),
    ], null);

    /// <summary>Maps one issues-list item to a row in <see cref="Schema"/>'s field order. Does not
    /// check for `pull_request` -- a caller that must skip pull requests uses <see cref="TryToRow"/>
    /// instead.</summary>
    public static object?[] ToRow(IssueDto dto) =>
    [
        dto.Id,
        dto.Number,
        dto.Title,
        dto.State,
        dto.User?.Login,
        dto.Body,
        SerializeNames(dto.Labels?.Select(l => l.Name)),
        SerializeNames(dto.Assignees?.Select(a => a.Login)),
        dto.Milestone?.Title,
        dto.Comments,
        GithubTimestamps.Parse(dto.CreatedAt),
        GithubTimestamps.Parse(dto.UpdatedAt),
        GithubTimestamps.Parse(dto.ClosedAt),
        dto.HtmlUrl,
    ];

    /// <summary>Skips items that are actually pull requests: GitHub's issues-list endpoint returns
    /// both, distinguished only by a non-null `pull_request` field on the raw item. Returns `false`
    /// (and a null row) for one instead of mapping it -- the caller simply never appends that row, so
    /// no batch-cutting side effect happens either way.</summary>
    public static bool TryToRow(IssueDto dto, out object?[]? row)
    {
        if (dto.PullRequest is not null)
        {
            row = null;
            return false;
        }

        row = ToRow(dto);
        return true;
    }

    /// <summary>Serializes a `labels[].name`/`assignees[].login` projection as a JSON array of
    /// strings (e.g. `["bug","p1"]`). A missing or empty source collection still yields `"[]"`,
    /// never a JSON null -- GitHub always sends an array for these fields, but this stays defensive
    /// either way.</summary>
    private static string SerializeNames(IEnumerable<string?>? names) =>
        JsonSerializer.Serialize((names ?? []).ToArray(), GithubJsonContext.Default.StringArray);
}
