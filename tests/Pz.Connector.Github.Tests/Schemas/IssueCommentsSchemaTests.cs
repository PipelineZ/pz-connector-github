using Apache.Arrow.Types;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github.Tests;

public sealed class IssueCommentsSchemaTests
{
    private static IssueCommentDto FullDto(string? issueUrl = "https://api.github.com/repos/o/r/issues/42") => new(
        Id: 9001,
        IssueUrl: issueUrl,
        User: new GithubUserDto("octocat"),
        Body: "Looks good to me",
        CreatedAt: "2024-01-15T10:30:00Z",
        UpdatedAt: "2024-01-15T11:00:00Z",
        HtmlUrl: "https://github.com/o/r/issues/42#issuecomment-9001");

    [Fact]
    public void Schema_has_7_fields_in_spec_order_with_expected_types()
    {
        var fields = IssueCommentsSchema.Schema.FieldsList;
        string[] expectedNames = ["id", "issue_number", "author", "body", "created_at", "updated_at", "html_url"];

        Assert.Equal(7, fields.Count);
        Assert.Equal(expectedNames, fields.Select(f => f.Name));

        Assert.Equal(ArrowTypeId.Int64, fields[0].DataType.TypeId);
        Assert.Equal(ArrowTypeId.Int32, fields[1].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[2].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[3].DataType.TypeId);

        foreach (var i in new[] { 4, 5 })
        {
            Assert.Equal(ArrowTypeId.Timestamp, fields[i].DataType.TypeId);
            var ts = (TimestampType)fields[i].DataType;
            Assert.Equal(TimeUnit.Microsecond, ts.Unit);
            Assert.Equal("UTC", ts.Timezone);
        }

        Assert.Equal(ArrowTypeId.String, fields[6].DataType.TypeId);
        Assert.All(fields, f => Assert.True(f.IsNullable));
    }

    [Fact]
    public void ToRow_maps_every_column_from_a_full_dto()
    {
        var row = IssueCommentsSchema.ToRow(FullDto());

        SchemaAssert.RowEquals(IssueCommentsSchema.Schema,
        [
            9001L,
            42,
            "octocat",
            "Looks good to me",
            new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 15, 11, 0, 0, TimeSpan.Zero),
            "https://github.com/o/r/issues/42#issuecomment-9001",
        ], row);
    }

    [Fact]
    public void ToRow_null_user_gives_null_author()
    {
        var dto = FullDto() with { User = null };
        var row = IssueCommentsSchema.ToRow(dto);

        Assert.Null(row[2]);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/o/r/issues/42", 42)]
    [InlineData("https://api.github.com/repos/o/r/pulls/42", 42)] // endpoint always uses /issues/{n}; robust either way
    [InlineData("https://api.github.com/repos/o/r/issues/1", 1)]
    public void ParseIssueNumber_extracts_the_trailing_segment(string issueUrl, int expected)
    {
        Assert.Equal(expected, IssueCommentsSchema.ParseIssueNumber(9001, issueUrl));
    }

    [Fact]
    public void ParseIssueNumber_malformed_url_throws_naming_the_comment_id()
    {
        var ex = Assert.Throws<PzConnectorException>(() => IssueCommentsSchema.ParseIssueNumber(9001, "not-a-number"));

        Assert.False(ex.IsTransient);
        Assert.Contains("9001", ex.Message);
        Assert.Contains("not-a-number", ex.Message);
    }

    [Fact]
    public void ParseIssueNumber_null_url_throws_naming_the_comment_id()
    {
        var ex = Assert.Throws<PzConnectorException>(() => IssueCommentsSchema.ParseIssueNumber(9001, null));

        Assert.False(ex.IsTransient);
        Assert.Contains("9001", ex.Message);
    }

    [Fact]
    public void ToRow_malformed_issue_url_throws_naming_the_comment_id()
    {
        var dto = FullDto(issueUrl: "garbage");

        var ex = Assert.Throws<PzConnectorException>(() => IssueCommentsSchema.ToRow(dto));

        Assert.False(ex.IsTransient);
        Assert.Contains("9001", ex.Message);
        Assert.Contains("garbage", ex.Message);
    }
}
