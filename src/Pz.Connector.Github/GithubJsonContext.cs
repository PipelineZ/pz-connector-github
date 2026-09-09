using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pz.Connector.Github;

/// <summary>Source-generated JSON context for GitHub connector. JsonElement is a minimal shell kept
/// for the `pull_request` presence check (<see cref="IssueDto.PullRequest"/>); every fixed-schema
/// entity's DTOs (issues, pulls, issue/PR comments, commits, releases, Actions runs) are registered
/// explicitly, including nested types, plus `string[]` for the `labels`/`assignees` JSON-array
/// serialization in <c>IssuesSchema</c>.</summary>
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(GithubUserDto))]
[JsonSerializable(typeof(IssueDto))]
[JsonSerializable(typeof(LabelDto))]
[JsonSerializable(typeof(MilestoneDto))]
[JsonSerializable(typeof(PullDto))]
[JsonSerializable(typeof(PullRefDto))]
[JsonSerializable(typeof(IssueCommentDto))]
[JsonSerializable(typeof(CommitDto))]
[JsonSerializable(typeof(CommitDetailDto))]
[JsonSerializable(typeof(GitUserDto))]
[JsonSerializable(typeof(ReleaseDto))]
[JsonSerializable(typeof(ActionsRunDto))]
[JsonSerializable(typeof(RepoDto))]
[JsonSerializable(typeof(IssueDto[]))]
[JsonSerializable(typeof(PullDto[]))]
[JsonSerializable(typeof(IssueCommentDto[]))]
[JsonSerializable(typeof(CommitDto[]))]
[JsonSerializable(typeof(ReleaseDto[]))]
[JsonSerializable(typeof(ActionsRunDto[]))]
internal sealed partial class GithubJsonContext : JsonSerializerContext;

/// <summary>The repo-detail lookup used only to resolve a commits dataset's default branch when no
/// `ref:` was configured (<see cref="GithubSource"/>). Every other field GitHub sends is ignored.</summary>
internal sealed record RepoDto([property: JsonPropertyName("default_branch")] string? DefaultBranch);
