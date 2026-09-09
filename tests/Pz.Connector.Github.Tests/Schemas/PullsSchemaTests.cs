using Apache.Arrow.Types;

namespace Pz.Connector.Github.Tests;

public sealed class PullsSchemaTests
{
    private static PullDto FullDto() => new(
        Id: 555,
        Number: 7,
        Title: "Add feature",
        State: "open",
        User: new GithubUserDto("octocat"),
        Body: "Description",
        Draft: false,
        Base: new PullRefDto("main"),
        Head: new PullRefDto("feature-branch"),
        MergedAt: null,
        CreatedAt: "2024-01-20T08:00:00Z",
        UpdatedAt: "2024-01-25T08:00:00Z",
        ClosedAt: null,
        HtmlUrl: "https://github.com/o/r/pull/7");

    [Fact]
    public void Schema_has_14_fields_in_spec_order_with_expected_types()
    {
        var fields = PullsSchema.Schema.FieldsList;
        string[] expectedNames =
        [
            "id", "number", "title", "state", "author", "body", "draft", "base_ref", "head_ref",
            "merged_at", "created_at", "updated_at", "closed_at", "html_url",
        ];

        Assert.Equal(14, fields.Count);
        Assert.Equal(expectedNames, fields.Select(f => f.Name));

        Assert.Equal(ArrowTypeId.Int64, fields[0].DataType.TypeId);
        Assert.Equal(ArrowTypeId.Int32, fields[1].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[2].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[3].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[4].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[5].DataType.TypeId);
        Assert.Equal(ArrowTypeId.Boolean, fields[6].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[7].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[8].DataType.TypeId);
        foreach (var i in new[] { 9, 10, 11, 12 })
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
        // MergedAt and ClosedAt are deliberately DIFFERENT timestamps here (unlike every sibling
        // kind's equivalent full-column test): a transposition bug swapping which row-array slot
        // each maps to would pass undetected if both used the same value.
        var dto = FullDto() with { MergedAt = "2024-02-01T00:00:00Z", ClosedAt = "2024-02-03T00:00:00Z" };
        var row = PullsSchema.ToRow(dto);

        SchemaAssert.RowEquals(PullsSchema.Schema,
        [
            555L,
            7,
            "Add feature",
            "open",
            "octocat",
            "Description",
            false,
            "main",
            "feature-branch",
            new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 20, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 25, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 2, 3, 0, 0, 0, TimeSpan.Zero),
            "https://github.com/o/r/pull/7",
        ], row);
    }

    [Fact]
    public void ToRow_draft_true_maps_boolean_column()
    {
        var dto = FullDto() with { Draft = true };
        var row = PullsSchema.ToRow(dto);

        Assert.Equal(true, row[6]);
    }

    [Fact]
    public void ToRow_merged_at_null_on_an_open_pr()
    {
        var row = PullsSchema.ToRow(FullDto());

        Assert.Null(row[9]);
    }

    [Fact]
    public void ToRow_base_ref_and_head_ref_from_nested_objects()
    {
        var dto = FullDto() with { Base = new PullRefDto("develop"), Head = new PullRefDto("my-branch") };
        var row = PullsSchema.ToRow(dto);

        Assert.Equal("develop", row[7]);
        Assert.Equal("my-branch", row[8]);
    }

    [Fact]
    public void ToRow_null_base_or_head_gives_null_ref_columns()
    {
        var dto = FullDto() with { Base = null, Head = null };
        var row = PullsSchema.ToRow(dto);

        Assert.Null(row[7]);
        Assert.Null(row[8]);
    }

    [Fact]
    public void ToRow_null_user_gives_null_author()
    {
        var dto = FullDto() with { User = null };
        var row = PullsSchema.ToRow(dto);

        Assert.Null(row[4]);
    }
}
