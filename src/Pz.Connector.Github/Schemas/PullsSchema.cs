using System.Text.Json.Serialization;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Pz.Connector.Github;

/// <summary>One item off GitHub's pull-requests-list endpoint.</summary>
internal sealed record PullDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("user")] GithubUserDto? User,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("base")] PullRefDto? Base,
    [property: JsonPropertyName("head")] PullRefDto? Head,
    [property: JsonPropertyName("merged_at")] string? MergedAt,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt,
    [property: JsonPropertyName("closed_at")] string? ClosedAt,
    [property: JsonPropertyName("html_url")] string? HtmlUrl);

/// <summary>The `base`/`head` object on a pull request -- only `ref` (the branch name) is used.</summary>
internal sealed record PullRefDto([property: JsonPropertyName("ref")] string? Ref);

/// <summary>Arrow shape for the `pulls` entity (spec §6.2) and the pure DTO-to-row mapping
/// `GithubPartition` (Task 7) calls per page item. Every column is nullable.</summary>
internal static class PullsSchema
{
    public static readonly Schema Schema = new(
    [
        new Field("id", Int64Type.Default, nullable: true),
        new Field("number", Int32Type.Default, nullable: true),
        new Field("title", StringType.Default, nullable: true),
        new Field("state", StringType.Default, nullable: true),
        new Field("author", StringType.Default, nullable: true),
        new Field("body", StringType.Default, nullable: true),
        new Field("draft", BooleanType.Default, nullable: true),
        new Field("base_ref", StringType.Default, nullable: true),
        new Field("head_ref", StringType.Default, nullable: true),
        new Field("merged_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("created_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("updated_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("closed_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("html_url", StringType.Default, nullable: true),
    ], null);

    /// <summary>Maps one pull-requests-list item to a row in <see cref="Schema"/>'s field order.</summary>
    public static object?[] ToRow(PullDto dto) =>
    [
        dto.Id,
        dto.Number,
        dto.Title,
        dto.State,
        dto.User?.Login,
        dto.Body,
        dto.Draft,
        dto.Base?.Ref,
        dto.Head?.Ref,
        GithubTimestamps.Parse(dto.MergedAt),
        GithubTimestamps.Parse(dto.CreatedAt),
        GithubTimestamps.Parse(dto.UpdatedAt),
        GithubTimestamps.Parse(dto.ClosedAt),
        dto.HtmlUrl,
    ];
}
