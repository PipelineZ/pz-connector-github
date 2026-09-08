using System.Text.Json.Serialization;

namespace Pz.Connector.Github;

/// <summary>The `user` object GitHub embeds on issues, pull requests, and comments -- shared by
/// every schema in this connector since only `login` is ever read off it.</summary>
internal sealed record GithubUserDto([property: JsonPropertyName("login")] string? Login);
