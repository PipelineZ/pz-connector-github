namespace Pz.Connector.Github;

/// <summary>Extracts the <c>rel="next"</c> URL from a GitHub API response's RFC 8288 <c>Link</c>
/// header for cursor-based pagination. A missing header, a header with no <c>next</c> relation, and
/// a malformed entry all resolve to <see langword="null"/> rather than throwing -- one bad entry must
/// never abort a page walk.</summary>
internal static class LinkHeaderPaginator
{
    public static string? NextUrl(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var headerValues))
        {
            return null;
        }

        foreach (var headerValue in headerValues)
        {
            foreach (var rawEntry in headerValue.Split(','))
            {
                var entry = rawEntry.Trim();
                if (!entry.StartsWith('<'))
                {
                    continue;
                }

                var urlEnd = entry.IndexOf('>');
                if (urlEnd < 0)
                {
                    continue;
                }

                var url = entry[1..urlEnd];
                if (FindRel(entry[(urlEnd + 1)..]) == "next")
                {
                    return url;
                }
            }
        }

        return null;
    }

    /// <summary>Finds the <c>rel</c> parameter's value among a Link entry's <c>; name="value"</c>
    /// pairs (the part after the <c>&lt;url&gt;</c>). Quotes around the value are optional per
    /// RFC 8288 and stripped either way; a pair with no <c>=</c> is skipped, not an error -- it just
    /// isn't the one we're looking for.</summary>
    private static string? FindRel(string parameters)
    {
        foreach (var rawParam in parameters.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = rawParam.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            if (rawParam[..eq].Trim() != "rel")
            {
                continue;
            }

            return rawParam[(eq + 1)..].Trim().Trim('"');
        }

        return null;
    }
}
