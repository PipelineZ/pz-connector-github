using System.Net;
using System.Text;

namespace Pz.Connector.Github.Tests;

/// <summary>Scripted <see cref="HttpMessageHandler"/> double for read-loop tests: every request is
/// resolved by its path+query against a canned response, never touching a socket. An unmapped
/// path+query throws rather than silently answering 404 -- a test whose expected URL construction is
/// wrong must fail loudly, not pass by accident against a default response.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    /// <summary>Every request's full URL, in the order received.</summary>
    public List<Uri> Requests { get; } = [];

    public void Map(string pathAndQuery, HttpResponseMessage response) => _routes[pathAndQuery] = _ => response;

    public void Map(string pathAndQuery, Func<HttpRequestMessage, HttpResponseMessage> handler) => _routes[pathAndQuery] = handler;

    /// <summary>Builds a JSON response, optionally carrying a <c>Link</c> header (pass the whole
    /// header value, e.g. <c>&lt;https://api.github.com/x?page=2&gt;; rel="next"</c>).</summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string body, string? linkHeader = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (linkHeader is not null)
        {
            response.Headers.Add("Link", linkHeader);
        }

        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        Requests.Add(uri);

        if (!_routes.TryGetValue(uri.PathAndQuery, out var handler))
        {
            throw new InvalidOperationException($"FakeHandler: no route mapped for '{uri.PathAndQuery}' (full url: {uri})");
        }

        return Task.FromResult(handler(request));
    }
}
