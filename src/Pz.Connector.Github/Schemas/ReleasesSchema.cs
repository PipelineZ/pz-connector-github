using System.Text.Json.Serialization;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Pz.Connector.Github;

/// <summary>One item off GitHub's releases-list endpoint.</summary>
internal sealed record ReleaseDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("author")] GithubUserDto? Author,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("published_at")] string? PublishedAt,
    [property: JsonPropertyName("html_url")] string? HtmlUrl);

/// <summary>Arrow shape for the `releases` entity (spec §6.5) and the pure DTO-to-row mapping
/// `GithubPartition` (Task 7) calls per page item. Every column is nullable; `published_at` is null
/// on a draft release (GitHub sends `published_at: null` until the release is published).</summary>
internal static class ReleasesSchema
{
    public static readonly Schema Schema = new(
    [
        new Field("id", Int64Type.Default, nullable: true),
        new Field("tag_name", StringType.Default, nullable: true),
        new Field("name", StringType.Default, nullable: true),
        new Field("body", StringType.Default, nullable: true),
        new Field("draft", BooleanType.Default, nullable: true),
        new Field("prerelease", BooleanType.Default, nullable: true),
        new Field("author", StringType.Default, nullable: true),
        new Field("created_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("published_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("html_url", StringType.Default, nullable: true),
    ], null);

    /// <summary>Maps one releases-list item to a row in <see cref="Schema"/>'s field order.</summary>
    public static object?[] ToRow(ReleaseDto dto) =>
    [
        dto.Id,
        dto.TagName,
        dto.Name,
        dto.Body,
        dto.Draft,
        dto.Prerelease,
        dto.Author?.Login,
        GithubTimestamps.Parse(dto.CreatedAt),
        GithubTimestamps.Parse(dto.PublishedAt),
        dto.HtmlUrl,
    ];
}
