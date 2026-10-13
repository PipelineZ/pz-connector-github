using System.Net;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github;

/// <summary>Maps GitHub's rate-limit accounting onto the connector stats. Every successful REST call
/// to these endpoints costs one rate-limit point, so cost is one <c>requests</c> unit per successful
/// call (page reads and the default-branch lookup); failed calls are not billed here. The remaining
/// quota is deliberately not reported -- extras are additive across partitions and attempts, so a
/// summed "remaining" gauge would be meaningless.</summary>
internal static class GithubRateLimit
{
    public static void ReportSuccess(IConnectorStats stats) => stats.AddCost(1, "requests");

    /// <summary>Counts GitHub's secondary limit (403 carrying Retry-After) and primary exhaustion
    /// (403 with <c>x-ratelimit-remaining: 0</c>). A 429 is already counted by the SDK's HTTP
    /// handler, so it is never counted here.</summary>
    public static void ReportFailure(HttpResponseMessage response, IConnectorStats stats)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return;
        }

        var exhausted = response.Headers.TryGetValues("x-ratelimit-remaining", out var values)
            && values.FirstOrDefault() == "0";
        if (response.Headers.RetryAfter is not null || exhausted)
        {
            stats.AddThrottled(1);
        }
    }
}
