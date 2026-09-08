using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pz.Connector.Github;

/// <summary>Source-generated JSON context for GitHub connector. Documents travel as <see cref="JsonElement"/>.</summary>
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class GithubJsonContext : JsonSerializerContext;
