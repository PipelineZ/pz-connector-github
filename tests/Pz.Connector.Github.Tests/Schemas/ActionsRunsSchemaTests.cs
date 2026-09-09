using Apache.Arrow.Types;

namespace Pz.Connector.Github.Tests;

public sealed class ActionsRunsSchemaTests
{
    private static ActionsRunDto FullDto() => new(
        Id: 12345,
        Name: "CI",
        RunNumber: 42,
        HeadBranch: "main",
        HeadSha: "abc123",
        Event: "push",
        Status: "completed",
        Conclusion: "success",
        CreatedAt: "2024-05-01T00:00:00Z",
        UpdatedAt: "2024-05-01T00:10:00Z",
        RunStartedAt: "2024-05-01T00:01:00Z",
        HtmlUrl: "https://github.com/o/r/actions/runs/12345");

    [Fact]
    public void Schema_has_12_fields_in_spec_order_with_expected_types()
    {
        var fields = ActionsRunsSchema.Schema.FieldsList;
        string[] expectedNames =
        [
            "id", "name", "run_number", "head_branch", "head_sha", "event", "status", "conclusion",
            "created_at", "updated_at", "run_started_at", "html_url",
        ];

        Assert.Equal(12, fields.Count);
        Assert.Equal(expectedNames, fields.Select(f => f.Name));

        Assert.Equal(ArrowTypeId.Int64, fields[0].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[1].DataType.TypeId);
        Assert.Equal(ArrowTypeId.Int32, fields[2].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[3].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[4].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[5].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[6].DataType.TypeId);
        Assert.Equal(ArrowTypeId.String, fields[7].DataType.TypeId);

        foreach (var i in new[] { 8, 9, 10 })
        {
            Assert.Equal(ArrowTypeId.Timestamp, fields[i].DataType.TypeId);
            var ts = (TimestampType)fields[i].DataType;
            Assert.Equal(TimeUnit.Microsecond, ts.Unit);
            Assert.Equal("UTC", ts.Timezone);
        }

        Assert.Equal(ArrowTypeId.String, fields[11].DataType.TypeId);
        Assert.All(fields, f => Assert.True(f.IsNullable));
    }

    [Fact]
    public void ToRow_maps_every_column_from_a_full_dto()
    {
        var dto = FullDto();
        var row = ActionsRunsSchema.ToRow(dto);

        SchemaAssert.RowEquals(ActionsRunsSchema.Schema,
        [
            12345L,
            "CI",
            42,
            "main",
            "abc123",
            "push",
            "completed",
            "success",
            new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 5, 1, 0, 10, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 5, 1, 0, 1, 0, TimeSpan.Zero),
            "https://github.com/o/r/actions/runs/12345",
        ], row);
    }

    [Fact]
    public void ToRow_conclusion_null_while_a_run_is_in_progress()
    {
        var dto = FullDto() with { Status = "in_progress", Conclusion = null };
        var row = ActionsRunsSchema.ToRow(dto);

        Assert.Null(row[7]);
        Assert.Equal("in_progress", row[6]);
    }
}
