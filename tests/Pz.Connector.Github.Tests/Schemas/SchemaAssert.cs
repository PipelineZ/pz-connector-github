using Apache.Arrow;

namespace Pz.Connector.Github.Tests;

/// <summary>Compares an actual `ToRow` output against an expected row array field-by-field, naming
/// the schema field on a mismatch instead of xunit's default whole-array diff.</summary>
internal static class SchemaAssert
{
    public static void RowEquals(Schema schema, object?[] expected, object?[] actual)
    {
        Assert.Equal(schema.FieldsList.Count, expected.Length);
        Assert.Equal(schema.FieldsList.Count, actual.Length);

        for (var i = 0; i < schema.FieldsList.Count; i++)
        {
            var name = schema.FieldsList[i].Name;
            Assert.True(Equals(expected[i], actual[i]),
                $"field '{name}' (index {i}): expected <{Format(expected[i])}>, got <{Format(actual[i])}>");
        }
    }

    private static string Format(object? value) => value?.ToString() ?? "null";
}
