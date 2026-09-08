using Apache.Arrow.Types;

namespace Pz.Connector.Github.Tests;

public sealed class ReleasesSchemaTests
{
    private static ReleaseDto FullDto() => new(
        Id: 9001,
        TagName: "v1.2.3",
        Name: "Version 1.2.3",
        Body: "Release notes",
        Draft: false,
        Prerelease: false,
        Author: new GithubUserDto("octocat"),
        CreatedAt: "2024-04-01T00:00:00Z",
        PublishedAt: "2024-04-02T00:00:00Z",
        HtmlUrl: "https://github.com/o/r/releases/tag/v1.2.3");

    [Fact]
    public void Schema_has_10_fields_in_spec_order_with_expected_types()
    {
        var fields = ReleasesSchema.Schema.FieldsList;
        string[] expectedNames =
        [
            "id", "tag_name", "name", "body", "draft", "prerelease", "author",
            "created_at", "published_at", "html_url",
        ];

        Assert.Equal(10, fields.Count);
        Assert.Equal(expectedNames, fields.Select(f => f.Name));

        Assert.Equal(ArrowTypeId.Int64, fields[0].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[1].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[2].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[3].DataType.TypeId);
        Assert.Equal(ArrowTypeId.Boolean, fields[4].DataType.TypeId);
        Assert.Equal(ArrowTypeId.Boolean, fields[5].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[6].DataType.TypeId);

        foreach (var i in new[] { 7, 8 })
        {
            Assert.Equal(ArrowTypeId.Timestamp, fields[i].DataType.TypeId);
            var ts = (TimestampType)fields[i].DataType;
            Assert.Equal(TimeUnit.Microsecond, ts.Unit);
            Assert.Equal("UTC", ts.Timezone);
        }

        Assert.Equal(ArrowTypeId.String, fields[9].DataType.TypeId);
        Assert.All(fields, f => Assert.True(f.IsNullable));
    }

    [Fact]
    public void ToRow_maps_every_column_from_a_full_dto()
    {
        var dto = FullDto();
        var row = ReleasesSchema.ToRow(dto);

        SchemaAssert.RowEquals(ReleasesSchema.Schema,
        [
            9001L,
            "v1.2.3",
            "Version 1.2.3",
            "Release notes",
            false,
            false,
            "octocat",
            new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 4, 2, 0, 0, 0, TimeSpan.Zero),
            "https://github.com/o/r/releases/tag/v1.2.3",
        ], row);
    }

    [Fact]
    public void ToRow_draft_and_prerelease_booleans()
    {
        var dto = FullDto() with { Draft = true, Prerelease = true };
        var row = ReleasesSchema.ToRow(dto);

        Assert.Equal(true, row[4]);
        Assert.Equal(true, row[5]);
    }

    [Fact]
    public void ToRow_published_at_null_on_a_draft_release()
    {
        var dto = FullDto() with { Draft = true, PublishedAt = null };
        var row = ReleasesSchema.ToRow(dto);

        Assert.Null(row[8]);
    }

    [Fact]
    public void ToRow_null_author_gives_null_author_column()
    {
        var dto = FullDto() with { Author = null };
        var row = ReleasesSchema.ToRow(dto);

        Assert.Null(row[6]);
    }
}
