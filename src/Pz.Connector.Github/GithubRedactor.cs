using System.Text.RegularExpressions;

namespace Pz.Connector.Github;

/// <summary>Strips credentials from any text that may reach a PzConnectorException message, a log
/// line, or a ConnectionCheck: every configured secret value is replaced wherever it occurs (a
/// GitHub error can echo a token anywhere, not only beside its key), and a bare
/// <c>Authorization: Bearer &lt;token&gt;</c> fragment -- GitHub only ever sends Bearer, never Basic
/// or ApiKey -- is rewritten even when the token is not one of ours (an echo in a diagnostic string
/// that was never registered as a secret). Secrets shorter than 3 characters are not matched;
/// replacing them would shred unrelated text.</summary>
internal sealed partial class GithubRedactor
{
    public const string Mask = "***";

    public static readonly GithubRedactor None = new([]);

    private readonly string[] _secrets;

    public GithubRedactor(IReadOnlyList<string> secrets)
    {
        _secrets = secrets.Where(s => s.Length >= 3).Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length).ToArray();
    }

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return AuthorizationHeader().Replace(text, m => $"{m.Groups["key"].Value} {Mask}");
    }

    // "Authorization: Bearer <token>" as an error message or diagnostic string might echo it back;
    // the token runs to the next whitespace.
    [GeneratedRegex("""(?<key>\bAuthorization:)\s+Bearer\s+\S+""", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();
}
