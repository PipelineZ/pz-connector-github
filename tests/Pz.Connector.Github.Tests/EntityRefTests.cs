namespace Pz.Connector.Github.Tests;

public sealed class EntityRefTests
{
    [Fact]
    public void Issues_parses_owner_repo_and_kind()
    {
        Assert.True(EntityRef.TryParse("PipelineZ/pz/issues", out var result));
        Assert.Equal("PipelineZ", result.Owner);
        Assert.Equal("pz", result.Repo);
        Assert.Equal(GithubEntityKind.Issues, result.Kind);
    }

    [Fact]
    public void Pulls_parses()
    {
        Assert.True(EntityRef.TryParse("PipelineZ/pz/pulls", out var result));
        Assert.Equal(GithubEntityKind.Pulls, result.Kind);
    }

    [Fact]
    public void Issue_comments_two_segment_kind_parses()
    {
        Assert.True(EntityRef.TryParse("PipelineZ/pz/issues/comments", out var result));
        Assert.Equal("PipelineZ", result.Owner);
        Assert.Equal("pz", result.Repo);
        Assert.Equal(GithubEntityKind.IssueComments, result.Kind);
    }

    [Fact]
    public void Commits_parses()
    {
        Assert.True(EntityRef.TryParse("PipelineZ/pz/commits", out var result));
        Assert.Equal(GithubEntityKind.Commits, result.Kind);
    }

    [Fact]
    public void Releases_parses()
    {
        Assert.True(EntityRef.TryParse("PipelineZ/pz/releases", out var result));
        Assert.Equal(GithubEntityKind.Releases, result.Kind);
    }

    [Fact]
    public void Actions_runs_two_segment_kind_parses()
    {
        Assert.True(EntityRef.TryParse("PipelineZ/pz/actions/runs", out var result));
        Assert.Equal("PipelineZ", result.Owner);
        Assert.Equal("pz", result.Repo);
        Assert.Equal(GithubEntityKind.ActionsRuns, result.Kind);
    }

    [Fact]
    public void Fewer_than_three_segments_fails()
    {
        Assert.False(EntityRef.TryParse("PipelineZ/pz", out var result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void Single_segment_fails()
    {
        Assert.False(EntityRef.TryParse("PipelineZ", out _));
    }

    [Fact]
    public void Unmatched_kind_fails()
    {
        Assert.False(EntityRef.TryParse("PipelineZ/pz/bogus", out var result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void Kind_matching_is_case_sensitive()
    {
        Assert.False(EntityRef.TryParse("PipelineZ/pz/Issues", out _));
        Assert.False(EntityRef.TryParse("PipelineZ/pz/ISSUES", out _));
    }
}
