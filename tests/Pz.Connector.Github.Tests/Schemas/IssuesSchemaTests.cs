using System.Text.Json;
using Apache.Arrow.Types;

namespace Pz.Connector.Github.Tests;

public sealed class IssuesSchemaTests
{
    private static IssueDto FullDto(JsonElement? pullRequest = null) => new(
        Id: 1001,
        Number: 42,
        Title: "Something broke",
        State: "open",
        User: new GithubUserDto("octocat"),
        Body: "Steps to reproduce...",
        Labels: [new LabelDto("bug"), new LabelDto("p1")],
        Assignees: [new GithubUserDto("alice"), new GithubUserDto("bob")],
        Milestone: new MilestoneDto("v1.0"),
        Comments: 3,
        CreatedAt: "2024-01-15T10:30:00Z",
        UpdatedAt: "2024-01-16T09:00:00Z",
        ClosedAt: "2024-01-17T12:00:00Z",
        HtmlUrl: "https://github.com/o/r/issues/42",
        PullRequest: pullRequest);

    [Fact]
    public void Schema_has_14_fields_in_spec_order_with_expected_types()
    {
        var fields = IssuesSchema.Schema.FieldsList;
        string[] expectedNames =
        [
            "id", "number", "title", "state", "author", "body", "labels", "assignees",
            "milestone", "comments", "created_at", "updated_at", "closed_at", "html_url",
        ];

        Assert.Equal(14, fields.Count);
        Assert.Equal(expectedNames, fields.Select(f => f.Name));

        Assert.Equal(ArrowTypeId.Int64, fields[0].DataType.TypeId);
        Assert.Equal(ArrowTypeId.Int32, fields[1].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[2].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[3].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[4].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[5].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[6].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[7].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[8].DataType.TypeId);
        Assert.Equal(ArrowTypeId.Int32, fields[9].DataType.TypeId);
        foreach (var i in new[] { 10, 11, 12 })
        {
            Assert.Equal(ArrowTypeId.Timestamp, fields[i].DataType.TypeId);
            var ts = (TimestampType)fields[i].DataType;
            Assert.Equal(TimeUnit.Microsecond, ts.Unit);
            Assert.Equal("UTC", ts.Timezone);
        }

        Assert.Equal(ArrowTypeId.String, fields[13].DataType.TypeId);
        Assert.All(fields, f => Assert.True(f.IsNullable));
    }

    [Fact]
    public void ToRow_maps_every_column_from_a_full_dto()
    {
        var row = IssuesSchema.ToRow(FullDto());

        SchemaAssert.RowEquals(IssuesSchema.Schema,
        [
            1001L,
            42,
            "Something broke",
            "open",
            "octocat",
            "Steps to reproduce...",
            "[\"bug\",\"p1\"]",
            "[\"alice\",\"bob\"]",
            "v1.0",
            3,
            new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 16, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 17, 12, 0, 0, TimeSpan.Zero),
            "https://github.com/o/r/issues/42",
        ], row);
    }

    [Fact]
    public void ToRow_null_milestone_body_and_closed_at_become_null_entries()
    {
        var dto = FullDto() with { Milestone = null, Body = null, ClosedAt = null };
        var row = IssuesSchema.ToRow(dto);

        Assert.Null(row[5]); // body
        Assert.Null(row[8]); // milestone
        Assert.Null(row[12]); // closed_at
    }

    [Fact]
    public void ToRow_empty_or_missing_labels_and_assignees_serialize_to_empty_json_array()
    {
        var dto = FullDto() with { Labels = [], Assignees = null };
        var row = IssuesSchema.ToRow(dto);

        Assert.Equal("[]", row[6]);
        Assert.Equal("[]", row[7]);
    }

    [Fact]
    public void ToRow_null_user_gives_null_author()
    {
        var dto = FullDto() with { User = null };
        var row = IssuesSchema.ToRow(dto);

        Assert.Null(row[4]);
    }

    [Fact]
    public void TryToRow_returns_false_and_null_row_when_pull_request_is_present()
    {
        var dto = FullDto(pullRequest: JsonDocument.Parse("{}").RootElement);

        Assert.False(IssuesSchema.TryToRow(dto, out var row));
        Assert.Null(row);
    }

    [Fact]
    public void TryToRow_returns_true_and_the_mapped_row_when_pull_request_is_absent()
    {
        var dto = FullDto();

        Assert.True(IssuesSchema.TryToRow(dto, out var row));
        Assert.NotNull(row);
        Assert.Equal(1001L, row![0]);
    }
}
