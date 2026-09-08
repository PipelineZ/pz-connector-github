namespace Pz.Connector.Github.Tests;

public sealed class LinkHeaderPaginatorTests
{
    private static HttpResponseMessage Response(params string[] linkHeaderValues)
    {
        var response = new HttpResponseMessage();
        foreach (var value in linkHeaderValues)
        {
            response.Headers.Add("Link", value);
        }

        return response;
    }

    [Fact]
    public void Single_next_link_returns_its_url()
    {
        using var response = Response("<https://api.github.com/x?page=2>; rel=\"next\"");

        Assert.Equal("https://api.github.com/x?page=2", LinkHeaderPaginator.NextUrl(response));
    }

    [Fact]
    public void Multiple_entries_in_one_header_value_pick_next_over_others()
    {
        using var response = Response(
            "<https://api.github.com/x?page=1>; rel=\"prev\", " +
            "<https://api.github.com/x?page=3>; rel=\"next\", " +
            "<https://api.github.com/x?page=5>; rel=\"last\"");

        Assert.Equal("https://api.github.com/x?page=3", LinkHeaderPaginator.NextUrl(response));
    }

    [Fact]
    public void Multi_value_header_with_prev_and_next_in_separate_values_finds_next()
    {
        using var response = Response(
            "<https://api.github.com/x?page=1>; rel=\"prev\"",
            "<https://api.github.com/x?page=3>; rel=\"next\"");

        Assert.Equal("https://api.github.com/x?page=3", LinkHeaderPaginator.NextUrl(response));
    }

    [Fact]
    public void Absent_header_returns_null()
    {
        using var response = new HttpResponseMessage();

        Assert.Null(LinkHeaderPaginator.NextUrl(response));
    }

    [Fact]
    public void Header_with_only_prev_and_last_returns_null()
    {
        using var response = Response(
            "<https://api.github.com/x?page=1>; rel=\"prev\", " +
            "<https://api.github.com/x?page=5>; rel=\"last\"");

        Assert.Null(LinkHeaderPaginator.NextUrl(response));
    }

    [Fact]
    public void Malformed_entry_missing_angle_brackets_is_skipped_not_thrown()
    {
        using var response = Response("https://api.github.com/x?page=2; rel=\"next\"");

        Assert.Null(LinkHeaderPaginator.NextUrl(response));
    }

    [Fact]
    public void Malformed_entry_missing_rel_is_skipped_not_thrown()
    {
        using var response = Response("<https://api.github.com/x?page=2>");

        Assert.Null(LinkHeaderPaginator.NextUrl(response));
    }

    [Fact]
    public void Malformed_entry_is_skipped_while_a_later_valid_entry_is_still_found()
    {
        using var response = Response(
            "not-a-link-entry, <https://api.github.com/x?page=3>; rel=\"next\"");

        Assert.Equal("https://api.github.com/x?page=3", LinkHeaderPaginator.NextUrl(response));
    }

    [Fact]
    public void Rel_without_quotes_is_recognized()
    {
        using var response = Response("<https://api.github.com/x?page=2>; rel=next");

        Assert.Equal("https://api.github.com/x?page=2", LinkHeaderPaginator.NextUrl(response));
    }
}
