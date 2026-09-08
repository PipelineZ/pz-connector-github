using System.Net;
using System.Net.Sockets;
using System.Text;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github.Tests;

public sealed class GithubErrorsTests
{
    private static readonly GithubRedactor NoSecrets = GithubRedactor.None;

    // ---- IsTransient: no HTTP answer at all ----

    [Fact]
    public void No_status_HttpRequestException_is_transient()
    {
        Assert.True(GithubErrors.IsTransient(null, null, new HttpRequestException()));
    }

    [Fact]
    public void No_status_SocketException_is_transient()
    {
        Assert.True(GithubErrors.IsTransient(null, null, new SocketException()));
    }

    [Fact]
    public void No_status_timeout_not_from_the_callers_token_is_transient()
    {
        // TaskCanceledException raised internally by the client on a timeout, not tied to any
        // caller-owned CancellationToken -- it is still an OperationCanceledException.
        Assert.True(GithubErrors.IsTransient(null, null, new TaskCanceledException("The request timed out")));
    }

    [Fact]
    public void No_status_and_no_exception_is_transient()
    {
        Assert.True(GithubErrors.IsTransient(null, null, null));
    }

    [Fact]
    public void No_status_unrecognized_exception_is_not_transient()
    {
        Assert.False(GithubErrors.IsTransient(null, null, new FormatException("bad")));
    }

    // ---- IsTransient: rate-limit-remaining header, any status ----

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void Rate_limit_remaining_zero_is_transient_regardless_of_status(HttpStatusCode status)
    {
        var headers = new Dictionary<string, string> { ["x-ratelimit-remaining"] = "0" };

        Assert.True(GithubErrors.IsTransient(status, headers, null));
    }

    [Fact]
    public void Rate_limit_remaining_nonzero_does_not_force_transience()
    {
        var headers = new Dictionary<string, string> { ["x-ratelimit-remaining"] = "42" };

        Assert.False(GithubErrors.IsTransient(HttpStatusCode.Forbidden, headers, null));
    }

    // ---- IsTransient: 429 (secondary/abuse rate limit), regardless of headers ----

    [Fact]
    public void Rate_limited_429_is_transient_even_without_any_rate_limit_headers()
    {
        Assert.True(GithubErrors.IsTransient(HttpStatusCode.TooManyRequests, null, null));
    }

    [Fact]
    public void Rate_limited_429_is_transient_with_rate_limit_headers_present_too()
    {
        var headers = new Dictionary<string, string> { ["x-ratelimit-remaining"] = "10" };

        Assert.True(GithubErrors.IsTransient(HttpStatusCode.TooManyRequests, headers, null));
    }

    // ---- IsTransient: 5xx ----

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void Server_error_statuses_are_transient(HttpStatusCode status)
    {
        Assert.True(GithubErrors.IsTransient(status, null, null));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void Other_client_statuses_are_not_transient(HttpStatusCode status)
    {
        Assert.False(GithubErrors.IsTransient(status, null, null));
    }

    // ---- FromResponseAsync: classification table ----

    [Fact]
    public async Task Status_401_is_non_transient_with_the_token_hint()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "list issues");

        Assert.False(ex.IsTransient);
        Assert.Null(ex.RetryAfter);
        Assert.Equal("github: list issues: unauthorized (HTTP 401); check token", ex.Message);
    }

    [Fact]
    public async Task Status_403_with_rate_limit_remaining_zero_is_transient_with_retry_after_the_reset()
    {
        var now = Epoch;
        var resetAt = now.AddSeconds(120);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset", resetAt.ToUnixTimeSeconds().ToString());

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(now), "list issues");

        Assert.True(ex.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
        Assert.Equal("github: list issues: rate limited (HTTP 403); retrying after the reset", ex.Message);
    }

    [Fact]
    public async Task Status_403_with_past_reset_clamps_retry_after_to_zero()
    {
        var now = Epoch;
        var resetAt = now.AddSeconds(-30);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset", resetAt.ToUnixTimeSeconds().ToString());

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(now), "list issues");

        Assert.True(ex.IsTransient);
        Assert.Equal(TimeSpan.Zero, ex.RetryAfter);
    }

    [Fact]
    public async Task Status_403_with_retry_after_seconds_and_nonzero_remaining_is_transient()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "10");
        response.Headers.TryAddWithoutValidation("Retry-After", "30");

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "create issue");

        Assert.True(ex.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.Equal("github: create issue: rate limited (HTTP 403); retrying after the reset", ex.Message);
    }

    [Fact]
    public async Task Status_403_with_neither_rate_limit_signal_is_non_transient_with_the_scopes_hint()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "list issues");

        Assert.False(ex.IsTransient);
        Assert.Null(ex.RetryAfter);
        Assert.Equal(
            "github: list issues: forbidden (HTTP 403); check token scopes (private repos need the repo scope)",
            ex.Message);
    }

    [Fact]
    public async Task Status_404_is_non_transient_with_the_owner_repo_hint()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound);

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "get repo");

        Assert.False(ex.IsTransient);
        Assert.Equal("github: get repo: not found (HTTP 404); check owner/repo and that the token can see it",
            ex.Message);
    }

    [Fact]
    public async Task Status_422_uses_the_body_message_verbatim()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"message":"Validation Failed: name is required"}""", Encoding.UTF8, "application/json"),
        };

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "create issue");

        Assert.False(ex.IsTransient);
        Assert.Equal("github: create issue: Validation Failed: name is required", ex.Message);
    }

    [Fact]
    public async Task Status_422_without_a_body_message_falls_back_to_the_generic_detail()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "create issue");

        Assert.Equal("github: create issue: unprocessable (HTTP 422)", ex.Message);
    }

    [Fact]
    public async Task Status_429_with_retry_after_is_transient_with_that_delay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "5");

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "list issues");

        Assert.True(ex.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);
        Assert.Equal("github: list issues: rate limited (HTTP 429); retrying after the reset", ex.Message);
    }

    [Fact]
    public async Task Status_429_without_retry_after_is_still_transient_with_no_retry_after_value()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "list issues");

        Assert.True(ex.IsTransient);
        Assert.Null(ex.RetryAfter);
        Assert.Equal("github: list issues: rate limited (HTTP 429); retrying after the reset", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.BadGateway, 502)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    [InlineData(HttpStatusCode.GatewayTimeout, 504)]
    public async Task Server_errors_are_transient_with_a_generic_detail(HttpStatusCode status, int code)
    {
        using var response = new HttpResponseMessage(status);

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "list issues");

        Assert.True(ex.IsTransient);
        Assert.Equal($"github: list issues: server error (HTTP {code})", ex.Message);
    }

    [Fact]
    public async Task Unmapped_status_uses_the_body_message_when_present()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)451)
        {
            Content = new StringContent("""{"message":"unavailable for legal reasons"}""", Encoding.UTF8, "application/json"),
        };

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "get repo");

        Assert.False(ex.IsTransient);
        Assert.Equal("github: get repo: unavailable for legal reasons", ex.Message);
    }

    [Fact]
    public async Task Unmapped_status_without_a_body_message_falls_back_to_the_status_code()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)451);

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "get repo");

        Assert.Equal("github: get repo: HTTP 451", ex.Message);
    }

    [Fact]
    public async Task Non_json_body_does_not_throw_and_leaves_the_message_null()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("<html>not json</html>", Encoding.UTF8, "text/html"),
        };

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "create issue");

        Assert.Equal("github: create issue: unprocessable (HTTP 422)", ex.Message);
    }

    [Fact]
    public async Task Empty_body_does_not_throw_and_leaves_the_message_null()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);

        var ex = await GithubErrors.FromResponseAsync(response, NoSecrets, new FakeTime(Epoch), "create issue");

        Assert.Equal("github: create issue: unprocessable (HTTP 422)", ex.Message);
    }

    [Fact]
    public async Task Body_message_containing_a_secret_is_redacted()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"message":"token ghp_leakedsecret123 is invalid"}""", Encoding.UTF8, "application/json"),
        };
        var redactor = new GithubRedactor(["ghp_leakedsecret123"]);

        var ex = await GithubErrors.FromResponseAsync(response, redactor, new FakeTime(Epoch), "create issue");

        Assert.DoesNotContain("ghp_leakedsecret123", ex.Message, StringComparison.Ordinal);
        Assert.Contains("token *** is invalid", ex.Message, StringComparison.Ordinal);
    }

    // ---- Wrap ----

    [Fact]
    public void Wrap_returns_an_existing_PzConnectorException_unchanged()
    {
        var already = new PzConnectorException("already classified", isTransient: true);

        var wrapped = GithubErrors.Wrap(already, NoSecrets, "list issues");

        Assert.Same(already, wrapped);
    }

    [Fact]
    public void Wrap_classifies_HttpRequestException_as_transient()
    {
        var ex = GithubErrors.Wrap(new HttpRequestException("connection reset"), NoSecrets, "list issues");

        Assert.True(ex.IsTransient);
        Assert.Equal("github: list issues: connection reset", ex.Message);
    }

    [Fact]
    public void Wrap_classifies_SocketException_as_transient()
    {
        var ex = GithubErrors.Wrap(new SocketException((int)SocketError.ConnectionRefused), NoSecrets, "list issues");

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public void Wrap_classifies_a_client_side_timeout_as_transient()
    {
        var ex = GithubErrors.Wrap(new TaskCanceledException("The request timed out"), NoSecrets, "list issues");

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public void Wrap_classifies_an_unrecognized_exception_as_non_transient()
    {
        var ex = GithubErrors.Wrap(new InvalidOperationException("boom"), NoSecrets, "list issues");

        Assert.False(ex.IsTransient);
        Assert.Equal("github: list issues: boom", ex.Message);
    }

    [Fact]
    public void Wrap_redacts_a_secret_in_the_exception_message()
    {
        var redactor = new GithubRedactor(["ghp_leakedsecret123"]);

        var ex = GithubErrors.Wrap(new HttpRequestException("token ghp_leakedsecret123 rejected"), redactor, "list issues");

        Assert.DoesNotContain("ghp_leakedsecret123", ex.Message, StringComparison.Ordinal);
    }

    private static readonly DateTimeOffset Epoch = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
