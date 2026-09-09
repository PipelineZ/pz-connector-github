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
        // A base URI's path is REPLACED (not appended to) when combined with an absolute-path
        // reference (RFC 3986 §5.3) -- so a GHES base carrying a path (`https://ghe.example.com/api/v3`)
        // would silently lose that path the moment any request path started with '/'. Normalizing to a
        // trailing slash here, paired with every request path in this connector being relative (no
        // leading '/'), makes every request APPEND to the base path instead.
        var baseUri = new Uri(config.Url.AbsoluteUri.TrimEnd('/') + "/");
        var client = new HttpClient { BaseAddress = baseUri };
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
