using System.Text.Json.Serialization;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Pz.Connector.Github;

/// <summary>One item off GitHub's commits-list endpoint.</summary>
internal sealed record CommitDto(
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("commit")] CommitDetailDto? Commit,
    [property: JsonPropertyName("author")] GithubUserDto? Author,
    [property: JsonPropertyName("html_url")] string? HtmlUrl);

/// <summary>The nested `commit` object -- the raw git commit data, as opposed to the top-level
/// `author`/`committer` fields (not present on this DTO) that link to GitHub accounts.</summary>
internal sealed record CommitDetailDto(
    [property: JsonPropertyName("author")] GitUserDto? Author,
    [property: JsonPropertyName("committer")] GitUserDto? Committer,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>The raw git identity shape (`commit.author`/`commit.committer`): a name/email pair plus
/// the timestamp of that identity's action, with no link to a GitHub account -- that link, when
/// GitHub can make one, is the DTO's separate top-level `author`/`committer` field
/// (<see cref="GithubUserDto"/>).</summary>
internal sealed record GitUserDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("date")] string? Date);

/// <summary>Arrow shape for the `commits` entity (spec §6.4) and the pure DTO-to-row mapping
/// `GithubPartition` (Task 7) calls per page item. Every column is nullable.</summary>
internal static class CommitsSchema
{
    public static readonly Schema Schema = new(
    [
        new Field("sha", StringType.Default, nullable: true),
        new Field("author_name", StringType.Default, nullable: true),
        new Field("author_email", StringType.Default, nullable: true),
        new Field("author_login", StringType.Default, nullable: true),
        new Field("committed_at", new TimestampType(TimeUnit.Microsecond, "UTC"), nullable: true),
        new Field("message", StringType.Default, nullable: true),
        new Field("html_url", StringType.Default, nullable: true),
    ], null);

    /// <summary>Maps one commits-list item to a row in <see cref="Schema"/>'s field order.
    /// `author_login` reads the DTO's top-level `author.login` (the linked GitHub account) -- distinct
    /// from `author_name`/`author_email`, which read `commit.author` (the raw git identity) --
    /// and is null when GitHub found no matching account for the commit's email.</summary>
    public static object?[] ToRow(CommitDto dto) =>
    [
        dto.Sha,
        dto.Commit?.Author?.Name,
        dto.Commit?.Author?.Email,
        dto.Author?.Login,
        GithubTimestamps.Parse(dto.Commit?.Committer?.Date),
        dto.Commit?.Message,
        dto.HtmlUrl,
    ];
}
