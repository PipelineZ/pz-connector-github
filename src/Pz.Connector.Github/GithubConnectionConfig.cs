using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github;

/// <summary>The typed connection surface: a base API URL (github.com by default, an enterprise
/// server's own host otherwise) and an optional personal-access/installation token. Follows
/// <c>KafkaConnectionConfig</c>'s parse-and-aggregate discipline -- every field is validated before
/// returning, and a single <see cref="List{T}"/> collects every error rather than failing fast.</summary>
internal sealed record GithubConnectionConfig(Uri Url, string? Token, GithubRedactor Redactor)
{
    private static readonly string[] KnownKeys = ["url", "token"];
    private static readonly Uri DefaultUrl = new("https://api.github.com");

    public static GithubConnectionConfig? Parse(ConnectorConfig config, List<string> errors)
    {
        var start = errors.Count;

        foreach (var key in config.Values.Keys.Where(k => !KnownKeys.Contains(k, StringComparer.Ordinal)))
        {
            errors.Add($"unknown connection key '{key}'; known keys: {string.Join(", ", KnownKeys)}");
        }

        var url = DefaultUrl;
        var rawUrl = config.GetString("url");
        if (!string.IsNullOrEmpty(rawUrl))
        {
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add("'url' must be an absolute http/https URL");
            }
            else
            {
                var trimmed = rawUrl.EndsWith('/') ? rawUrl[..^1] : rawUrl;
                url = new Uri(trimmed);
            }
        }

        string? token = null;
        var secrets = new List<string>();
        if (config.Values.TryGetValue("token", out var rawToken) && rawToken is not null)
        {
            var tokenValue = config.GetString("token");
            if (string.IsNullOrWhiteSpace(tokenValue))
            {
                errors.Add("'token' must not be empty");
            }
            else
            {
                token = tokenValue;
                secrets.Add(tokenValue);
            }
        }

        return errors.Count == start
            ? new GithubConnectionConfig(url, token, new GithubRedactor(secrets))
            : null;
    }
}
