using Apache.Arrow.Types;

namespace Pz.Connector.Github.Tests;

public sealed class CommitsSchemaTests
{
    private static CommitDto FullDto() => new(
        Sha: "abc123def456",
        Commit: new CommitDetailDto(
            Author: new GitUserDto("Ada Lovelace", "ada@example.com", null),
            Committer: new GitUserDto(null, null, "2024-03-01T12:00:00Z"),
            Message: "Fix the thing"),
        Author: new GithubUserDto("octocat"),
        HtmlUrl: "https://github.com/o/r/commit/abc123def456");

    [Fact]
    public void Schema_has_7_fields_in_spec_order_with_expected_types()
    {
        var fields = CommitsSchema.Schema.FieldsList;
        string[] expectedNames =
        [
            "sha", "author_name", "author_email", "author_login", "committed_at", "message", "html_url",
        ];

        Assert.Equal(7, fields.Count);
        Assert.Equal(expectedNames, fields.Select(f => f.Name));

        Assert.Equal(ArrowTypeId.String, fields[0].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[1].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[2].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[3].DataType.TypeId);

        Assert.Equal(ArrowTypeId.Timestamp, fields[4].DataType.TypeId);
        var ts = (TimestampType)fields[4].DataType;
        Assert.Equal(TimeUnit.Microsecond, ts.Unit);
        Assert.Equal("UTC", ts.Timezone);

        Assert.Equal(ArrowTypeId.String, fields[5].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[6].DataType.TypeId);
        Assert.All(fields, f => Assert.True(f.IsNullable));
    }

    [Fact]
    public void ToRow_maps_every_column_from_a_full_dto()
    {
        var dto = FullDto();
        var row = CommitsSchema.ToRow(dto);

        SchemaAssert.RowEquals(CommitsSchema.Schema,
        [
            "abc123def456",
            "Ada Lovelace",
            "ada@example.com",
            "octocat",
            new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero),
            "Fix the thing",
            "https://github.com/o/r/commit/abc123def456",
        ], row);
    }

    [Fact]
    public void ToRow_author_login_null_when_top_level_author_is_null_unlinked_email()
    {
        var dto = FullDto() with { Author = null };
        var row = CommitsSchema.ToRow(dto);

        Assert.Null(row[3]);
    }

    [Fact]
    public void ToRow_committed_at_from_commit_committer_date_not_commit_author_date()
    {
        var dto = FullDto();
        var row = CommitsSchema.ToRow(dto);

        Assert.Equal(new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero), row[4]);
    }

    [Fact]
    public void ToRow_author_name_and_email_from_commit_author_not_top_level_author()
    {
        var dto = FullDto();
        var row = CommitsSchema.ToRow(dto);

        Assert.Equal("Ada Lovelace", row[1]);
        Assert.Equal("ada@example.com", row[2]);
    }
}
