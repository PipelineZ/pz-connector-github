using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pz.Connector.Github;

/// <summary>Source-generated JSON context for GitHub connector. No application DTOs yet;
/// later tasks register connector-specific types as needed. JsonElement is a minimal shell
/// required for source generation (the generator needs at least one type to emit abstract members).</summary>
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class GithubJsonContext : JsonSerializerContext;
