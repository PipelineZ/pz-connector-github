using System.Globalization;
using System.Net;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github;

/// <summary>Maps GitHub's rate-limit accounting onto the connector stats. Cost is the growth of
/// <c>x-ratelimit-used</c> between consecutive pages of one partition, in <c>requests</c>: the counter
/// is the quota window's, so calls made by other clients of the same token inside the window are
/// included. The remaining quota is deliberately not reported -- extras are additive across partitions
/// and attempts, so a summed "remaining" gauge would be meaningless.</summary>
internal sealed class GithubRateLimit(IConnectorStats stats)
{
    private long? _previousUsed;

    /// <summary>Records one successful page. The first page only sets the baseline; a missing or
    /// unparseable header, or a counter that moved backwards (window reset), reports nothing.</summary>
    public void ReportPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-ratelimit-used", out var values)
            || !long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var used)
            || used < 0)
        {
            return;
        }

        if (_previousUsed is { } previous && used - previous >= 0)
        {
            var delta = used - previous;
            if (delta > 0)
            {
                stats.AddCost(delta, "requests");
            }
        }

        _previousUsed = used;
    }

    /// <summary>Counts GitHub's secondary (abuse) limit, signalled by a 403 carrying Retry-After. A
    /// 429 is already counted by the SDK's HTTP handler, so it is never counted here.</summary>
    public static void ReportFailure(HttpResponseMessage response, IConnectorStats stats)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.RetryAfter is not null)
        {
            stats.AddThrottled(1);
        }
    }
}
