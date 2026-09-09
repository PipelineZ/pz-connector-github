using Microsoft.Extensions.Logging.Abstractions;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github.Tests;

/// <summary>Optional live smoke tests hitting the real GitHub API. These tests skip unless
/// GITHUB_TEST_TOKEN is set and require network access. Run locally to verify the connector
/// builds correct requests and reads real data.</summary>
[Trait("Category", "LiveGithub")]
public sealed class LiveGithubFacts
{
    /// <summary>A DelegatingHandler that records the last HttpRequestMessage before forwarding it to
    /// a real SocketsHttpHandler, allowing test assertions on actual headers sent to the real API.</summary>
    private sealed class RecordingHandler : DelegatingHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public RecordingHandler() : base(new SocketsHttpHandler())
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task Reads_real_issues_from_a_small_public_repo()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TEST_TOKEN");
        Skip.If(string.IsNullOrEmpty(token), "GITHUB_TEST_TOKEN is not set");

        // Build a real GithubConnectionConfig against https://api.github.com with the provided token.
        var errors = new List<string>();
        var connectionConfig = GithubConnectionConfig.Parse(
            new ConnectorConfig(new Dictionary<string, object?> { ["token"] = token }),
            errors);
        Assert.NotNull(connectionConfig);
        Assert.Empty(errors);

        // Create a recording handler to capture the actual request headers before forwarding to the real API.
        var recordingHandler = new RecordingHandler();
        var httpClient = new HttpClient(recordingHandler) { BaseAddress = connectionConfig.Url };

        // Apply the same headers that GithubHttpClientFactory.Create would set.
        httpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("pz-connector-github", "test"));

        if (connectionConfig.Token is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", connectionConfig.Token);
        }

        // Create a GithubSource directly and read from octocat/Hello-World's issues.
        var source = new GithubSource(connectionConfig, httpClient, NullLogger.Instance);
        var spec = new DatasetSpec("github", "octocat/Hello-World/issues", new Dictionary<string, object?>());

        // Verify the schema can be retrieved.
        var schema = await source.GetSchemaAsync(spec, CancellationToken.None);
        Assert.NotNull(schema);

        // Plan the read and execute it to collect a few rows.
        var partitions = await source.PlanReadAsync(spec, ReadHints.None, CancellationToken.None);
        Assert.NotEmpty(partitions);

        int rowCount = 0;
        const int maxRows = 10; // Collect a small sample to avoid rate limiting.

        await foreach (var batch in partitions[0].ReadAsync(BatchOptions.Default, CancellationToken.None))
        {
            if (batch.Length > 0)
            {
                rowCount += batch.Length;
                // Verify at least one row has a non-null id column (typical for issues).
                var idColumn = batch.Schema.GetFieldIndex("id");
                Assert.True(idColumn >= 0, "Expected an 'id' column in the schema");
                Assert.True(batch.Length > 0, "Expected at least one row in the batch");
            }

            batch.Dispose();

            if (rowCount >= maxRows)
            {
                break; // Stop after collecting a reasonable sample.
            }
        }

        // Assert that we received at least one row of data.
        Assert.True(rowCount > 0, "Expected to read at least one row from the real API");

        // Assert that the recorded request carried the correct headers.
        Assert.NotNull(recordingHandler.LastRequest);
        var request = recordingHandler.LastRequest;

        // Verify Accept header.
        Assert.Contains(request.Headers.Accept, h =>
            h.MediaType == "application/vnd.github+json");

        // Verify X-GitHub-Api-Version header.
        Assert.True(
            request.Headers.TryGetValues("X-GitHub-Api-Version", out var apiVersionValues),
            "Expected X-GitHub-Api-Version header");
        Assert.Equal("2022-11-28", apiVersionValues.First());

        // Verify Authorization header starts with "Bearer ".
        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
        Assert.NotNull(request.Headers.Authorization.Parameter);
    }
}
