using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pz.Connector.Github;

/// <summary>Source-generated JSON context for GitHub connector. JsonElement is a minimal shell kept
/// for the `pull_request` presence check (<see cref="IssueDto.PullRequest"/>); every fixed-schema
/// entity's DTOs (issues, pulls, issue/PR comments) are registered explicitly, including nested
/// types, plus `string[]` for the `labels`/`assignees` JSON-array serialization in
/// <c>IssuesSchema</c>.</summary>
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(GithubUserDto))]
[JsonSerializable(typeof(IssueDto))]
[JsonSerializable(typeof(LabelDto))]
[JsonSerializable(typeof(MilestoneDto))]
[JsonSerializable(typeof(PullDto))]
[JsonSerializable(typeof(PullRefDto))]
[JsonSerializable(typeof(IssueCommentDto))]
internal sealed partial class GithubJsonContext : JsonSerializerContext;
