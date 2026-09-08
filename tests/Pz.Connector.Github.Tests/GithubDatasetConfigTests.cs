using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github.Tests;

public sealed class GithubDatasetConfigTests
{
    private static DatasetSpec Spec(Dictionary<string, object?> options, string dataset = "PipelineZ/pz/issues") =>
        new("github", dataset, options);

    [Fact]
    public void Defaults_entity_to_dataset_name_per_page_100_ref_null()
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(Spec([]), errors);

        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Equal(new EntityRef("PipelineZ", "pz", GithubEntityKind.Issues), config.Entity);
        Assert.Equal(100, config.PerPage);
        Assert.Null(config.Ref);
    }

    [Fact]
    public void Explicit_entity_overrides_the_dataset_name()
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(
            Spec(new() { ["entity"] = "PipelineZ/pz/pulls" }, dataset: "my_dataset"), errors);

        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Equal(new EntityRef("PipelineZ", "pz", GithubEntityKind.Pulls), config.Entity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Per_page_out_of_range_errors(int value)
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(Spec(new() { ["per_page"] = value }), errors);

        Assert.Null(config);
        Assert.Contains($"dataset 'PipelineZ/pz/issues': 'per_page' must be between 1 and 100; got {value}", errors);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Per_page_boundary_values_are_accepted(int value)
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(Spec(new() { ["per_page"] = value }), errors);

        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Equal(value, config.PerPage);
    }

    [Fact]
    public void Per_page_non_integer_errors()
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(Spec(new() { ["per_page"] = "many" }), errors);

        Assert.Null(config);
        Assert.Contains(errors, e => e.Contains("'per_page'") && e.Contains("integer"));
    }

    [Fact]
    public void Per_page_boolean_value_errors_instead_of_silently_converting_to_1_or_0()
    {
        // Convert.ToInt32(true) silently succeeds as 1 -- a typo'd `per_page: true` must fail loudly,
        // not silently request 1 row per page.
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(Spec(new() { ["per_page"] = true }), errors);

        Assert.Null(config);
        Assert.Contains(errors, e => e.Contains("'per_page'") && e.Contains("integer"));
    }

    [Fact]
    public void Ref_on_commits_entity_is_accepted()
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(
            Spec(new() { ["ref"] = "main" }, dataset: "PipelineZ/pz/commits"), errors);

        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Equal("main", config.Ref);
    }

    [Fact]
    public void Ref_on_non_commits_entity_is_refused_naming_the_kind()
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(
            Spec(new() { ["ref"] = "main" }, dataset: "PipelineZ/pz/issues"), errors);

        Assert.Null(config);
        Assert.Contains(
            "dataset 'PipelineZ/pz/issues': 'ref' is only valid for commits entities, not issues", errors);
    }

    [Fact]
    public void Ref_absent_leaves_ref_null_regardless_of_kind()
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(Spec([], dataset: "PipelineZ/pz/commits"), errors);

        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Null(config.Ref);
    }

    [Fact]
    public void Invalid_entity_string_surfaces_entity_ref_failure_with_valid_kinds_listed()
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(Spec([], dataset: "not-a-valid-entity"), errors);

        Assert.Null(config);
        Assert.Contains(
            "dataset 'not-a-valid-entity': 'entity' value 'not-a-valid-entity' is not a valid GitHub entity " +
            "reference (expected '{owner}/{repo}/{kind}' where kind is one of: issues, pulls, issues/comments, " +
            "commits, releases, actions/runs)", errors);
    }

    [Fact]
    public void Entity_owner_with_a_disallowed_character_surfaces_an_aggregated_parse_error()
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(Spec([], dataset: "owner?evil/pz/issues"), errors);

        Assert.Null(config);
        Assert.Contains(errors, e => e.Contains("not a valid GitHub entity reference"));
    }

    [Fact]
    public void Unknown_option_errors_naming_known_keys()
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(Spec(new() { ["bogus"] = "x" }), errors);

        Assert.Null(config);
        Assert.Contains(
            "dataset 'PipelineZ/pz/issues': unknown read option 'bogus'; known: entity, per_page, ref", errors);
    }

    [Fact]
    public void Errors_aggregate_bad_per_page_and_bad_ref_and_unknown_key()
    {
        var errors = new List<string>();
        var config = GithubDatasetConfig.Parse(
            Spec(new() { ["per_page"] = 0, ["ref"] = "main", ["bogus"] = "x" }, dataset: "PipelineZ/pz/issues"),
            errors);

        Assert.Null(config);
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("'per_page'"));
        Assert.Contains(errors, e => e.Contains("'ref'"));
        Assert.Contains(errors, e => e.Contains("'bogus'"));
    }
}
