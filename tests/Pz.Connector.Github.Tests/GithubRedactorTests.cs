namespace Pz.Connector.Github.Tests;

public sealed class GithubRedactorTests
{
    [Fact]
    public void Replaces_every_occurrence_of_every_secret()
    {
        var redactor = new GithubRedactor(["ghp_s3cret", "other"]);

        Assert.Equal("a *** b *** c ***", redactor.Redact("a ghp_s3cret b other c ghp_s3cret"));
    }

    [Fact]
    public void Masks_a_bearer_token_echoed_in_error_text_even_when_not_a_registered_secret()
    {
        var redactor = GithubRedactor.None;

        Assert.Equal("request failed: Authorization: *** was rejected",
            redactor.Redact("request failed: Authorization: Bearer ghp_unregisteredtoken123 was rejected"));
    }

    [Fact]
    public void Empty_and_short_secrets_are_ignored()
    {
        var redactor = new GithubRedactor(["", "ab"]);

        Assert.Equal("abc", redactor.Redact("abc"));
    }

    [Fact]
    public void None_is_identity()
    {
        Assert.Equal("x", GithubRedactor.None.Redact("x"));
    }

    [Fact]
    public void Null_and_empty_text_pass_through()
    {
        Assert.Null(GithubRedactor.None.Redact(null!));
        Assert.Equal(string.Empty, GithubRedactor.None.Redact(string.Empty));
    }

    [Fact]
    public void Longer_secret_containing_a_shorter_one_is_masked_first()
    {
        var redactor = new GithubRedactor(["tok", "ghp_tok_long"]);

        Assert.Equal("value=***", redactor.Redact("value=ghp_tok_long"));
    }
}
