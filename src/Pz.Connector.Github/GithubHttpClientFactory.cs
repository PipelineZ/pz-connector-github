using System.Net.Http.Headers;
using System.Reflection;

namespace Pz.Connector.Github;

/// <summary>Builds the client every GitHub REST call goes through: the pinned API version and a
/// well-formed User-Agent (GitHub rejects requests without one), plus a bearer Authorization header
/// only when a token was configured -- an unauthenticated client is a deliberately valid shape
/// (public data at the anonymous rate limit).</summary>
internal static class GithubHttpClientFactory
{
    private static readonly string Version = typeof(GithubHttpClientFactory).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static HttpClient Create(GithubConnectionConfig config)
    {
        var client = new HttpClient { BaseAddress = config.Url };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pz-connector-github", Version));

        if (config.Token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        }

        return client;
    }
}
