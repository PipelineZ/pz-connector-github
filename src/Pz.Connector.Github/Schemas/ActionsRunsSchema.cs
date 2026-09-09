using System.Text.Json.Serialization;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Pz.Connector.Github;

/// <summary>One item off GitHub's Actions workflow-runs-list endpoint.</summary>
internal sealed record ActionsRunDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("run_number")] int RunNumber,
    [property: JsonPropertyName("head_branch")] string? HeadBranch,
    [property: JsonPropertyName("head_sha")] string? HeadSha,
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("conclusion")] string? Conclusion,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt,
    [property: JsonPropertyName("run_started_at")] string? RunStartedAt,
    [property: JsonPropertyName("html_url")] string? HtmlUrl);

/// <summary>Arrow shape for the `actions/runs` entity (spec §6.6) and the pure DTO-to-row mapping
/// `GithubPartition` (Task 7) calls per page item. Every column is nullable; `conclusion` is null
/// while a run is in progress -- GitHub sends a JSON `null` for that field, not a sentinel string, so
/// <see cref="ActionsRunDto.Conclusion"/> is a plain nullable string.</summary>
internal static class ActionsRunsSchema
{
    public static readonly Schema Schema = new(
    [
        new Field("id", Int64Type.Default, nullable: true),
        new Field("name", StringType.Default, nullable: true),
        new Field("run_number", Int32Type.Default, nullable: true),
        new Field("head_branch", StringType.Default, nullable: true),
        new Field("head_sha", StringType.Default, nullable: true),
        new Field("event", StringType.Default, nullable: true),
        new Field("status", StringType.Default, nullable: true),
        new Field("conclusion", StringType.Default, nullable: true),
        new Field("created_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("updated_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("run_started_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("html_url", StringType.Default, nullable: true),
    ], null);

    /// <summary>Maps one workflow-runs-list item to a row in <see cref="Schema"/>'s field order.</summary>
    public static object?[] ToRow(ActionsRunDto dto) =>
    [
        dto.Id,
        dto.Name,
        dto.RunNumber,
        dto.HeadBranch,
        dto.HeadSha,
        dto.Event,
        dto.Status,
        dto.Conclusion,
        GithubTimestamps.Parse(dto.CreatedAt),
        GithubTimestamps.Parse(dto.UpdatedAt),
        GithubTimestamps.Parse(dto.RunStartedAt),
        dto.HtmlUrl,
    ];
}
