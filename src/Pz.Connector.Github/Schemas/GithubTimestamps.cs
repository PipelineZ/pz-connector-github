using System.Globalization;

namespace Pz.Connector.Github;

/// <summary>Parses GitHub's `Z`-suffixed ISO-8601 UTC timestamp strings into the value
/// <c>ArrowBatchBuilder</c>'s Timestamp column appender expects (a raw <see cref="DateTimeOffset"/>,
/// boxed by the caller's row array). GitHub always sends UTC timestamps for every field this
/// connector reads, so no other offset form is handled.</summary>
internal static class GithubTimestamps
{
    public static DateTimeOffset? Parse(string? value) => value is null
        ? null
        : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
