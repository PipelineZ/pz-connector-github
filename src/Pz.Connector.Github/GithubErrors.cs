using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github;

/// <summary>Turns GitHub REST API failures into the engine's exception, classified for retry.
/// Transient = the API or the network may recover on its own: no HTTP answer at all, a hit against
/// the primary rate limit (<c>x-ratelimit-remaining: 0</c>, whatever the status code), the
/// secondary/abuse-detection limit (a 403 carrying <c>Retry-After</c>), and 500/502/503/504.
/// Everything about credentials, scopes, missing resources, and malformed requests is not. Messages
/// always pass the redactor -- a rejection can echo a bearer token back in its body.</summary>
internal static class GithubErrors
{
    /// <summary>Classifies a response/exception outcome for retry given only the status, an optional
    /// rate-limit-headers view (only <c>"x-ratelimit-remaining"</c> is consulted, keyed exactly that
    /// way), and the original exception when there was no HTTP answer at all. The secondary-limit
    /// <c>Retry-After</c> signal is deliberately NOT handled here: it makes a 403 transient only in
    /// combination with the status code, and folding that combination into this method's signature
    /// (shared with the no-response and generic-5xx cases) would obscure it rather than clarify it.
    /// <see cref="FromResponseAsync"/> applies that one extra rule directly against the response.</summary>
    public static bool IsTransient(HttpStatusCode? status, IReadOnlyDictionary<string, string>? rateLimitHeaders,
        Exception? original)
    {
        if (status is null)
        {
            // No answer at all: refused, reset, or timed out inside the client. The engine's own
            // cancellation never reaches here -- callers rethrow a bare OperationCanceledException
            // raised from their own token before ever calling into GithubErrors.
            return original is HttpRequestException or SocketException or OperationCanceledException or null;
        }

        if (rateLimitHeaders is not null
            && rateLimitHeaders.TryGetValue("x-ratelimit-remaining", out var remaining)
            && remaining == "0")
        {
            return true;
        }

        return status is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
    }

    /// <summary>Builds the classified exception for a non-success HTTP response. Reads the
    /// rate-limit/Retry-After headers and the JSON body's <c>message</c> field before returning --
    /// nothing here assumes the response outlives the call.</summary>
    public static async Task<PzConnectorException> FromResponseAsync(HttpResponseMessage response,
        GithubRedactor redactor, TimeProvider timeProvider, string context)
    {
        var status = response.StatusCode;
        var remaining = FirstHeader(response, "x-ratelimit-remaining");
        var reset = FirstHeader(response, "x-ratelimit-reset");
        var retryAfterHeader = ParseRetryAfterHeader(response, timeProvider);
        var message = await TryReadMessageAsync(response).ConfigureAwait(false);

        var rateLimited = remaining == "0";

        TimeSpan? retryAfter = null;
        if (rateLimited && long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetSeconds))
        {
            var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
            var delta = resetAt - timeProvider.GetUtcNow();
            retryAfter = delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        else if (retryAfterHeader is not null)
        {
            retryAfter = retryAfterHeader;
        }

        var rateLimitHeaders = remaining is null
            ? null
            : new Dictionary<string, string> { ["x-ratelimit-remaining"] = remaining };
        var transient = IsTransient(status, rateLimitHeaders, null)
            || (status == HttpStatusCode.Forbidden && retryAfterHeader is not null);

        var rateLimitedForbidden = status == HttpStatusCode.Forbidden && (rateLimited || retryAfterHeader is not null);
        var detail = status switch
        {
            HttpStatusCode.Unauthorized => "unauthorized (HTTP 401); check token",
            HttpStatusCode.Forbidden when rateLimitedForbidden => "rate limited (HTTP 403); retrying after the reset",
            HttpStatusCode.Forbidden => "forbidden (HTTP 403); check token scopes (private repos need the repo scope)",
            HttpStatusCode.NotFound => "not found (HTTP 404); check owner/repo and that the token can see it",
            HttpStatusCode.UnprocessableEntity => message ?? "unprocessable (HTTP 422)",
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                $"server error (HTTP {(int)status})",
            _ => message ?? $"HTTP {(int)status}",
        };

        return new PzConnectorException(redactor.Redact($"github: {context}: {detail}"), transient, retryAfter,
            innerException: null);
    }

    /// <summary>Wraps a non-HTTP-response failure (a network error before any response arrived) into
    /// the engine's exception. NOT for the engine's own cancellation: a bare
    /// <see cref="OperationCanceledException"/> raised from the caller's token must propagate
    /// unwrapped, so callers check <c>ct</c> and rethrow before ever reaching this method -- it never
    /// runs the classification a caller-cancelled run needs skipped.</summary>
    public static PzConnectorException Wrap(Exception ex, GithubRedactor redactor, string context)
    {
        if (ex is PzConnectorException already)
        {
            return already;
        }

        return new PzConnectorException(redactor.Redact($"github: {context}: {ex.Message}"),
            IsTransient(null, null, ex), innerException: ex);
    }

    /// <summary>Reads <c>{"message": "..."}</c> from the response body, best-effort: a non-JSON body,
    /// an empty body, or a JSON value that isn't that shape all just mean no message, never a
    /// thrown exception.</summary>
    private static async Task<string?> TryReadMessageAsync(HttpResponseMessage response)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FirstHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>GitHub's secondary/abuse-detection limit sends <c>Retry-After</c> in seconds; it may
    /// arrive as .NET's strongly-typed header or, depending on how a client sent it, only as a raw
    /// value -- both are checked. A date form converts to a delta from <paramref name="timeProvider"/>;
    /// either form clamps a past value to zero rather than going negative.</summary>
    private static TimeSpan? ParseRetryAfterHeader(HttpResponseMessage response, TimeProvider timeProvider)
    {
        var typed = response.Headers.RetryAfter;
        if (typed?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (typed?.Date is { } date)
        {
            var deltaFromDate = date - timeProvider.GetUtcNow();
            return deltaFromDate < TimeSpan.Zero ? TimeSpan.Zero : deltaFromDate;
        }

        var raw = FirstHeader(response, "Retry-After");
        if (raw is null)
        {
            return null;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds < 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
        {
            var deltaFromRaw = when - timeProvider.GetUtcNow();
            return deltaFromRaw < TimeSpan.Zero ? TimeSpan.Zero : deltaFromRaw;
        }

        return null;
    }
}
