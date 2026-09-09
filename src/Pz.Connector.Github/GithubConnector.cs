using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pz.Connectors.Abstractions;

namespace Pz.Connector.Github;

/// <summary>GitHub source connector for pz: reads issues, pull requests, comments, commits, releases, and Actions workflow runs.</summary>
public sealed class GithubConnector : IConnector, ISourceConnector
{
    private readonly ILoggerFactory _loggerFactory;

    public GithubConnector(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public ConnectorInfo Info { get; } = new(
        "github",
        typeof(GithubConnector).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0",
        ProtocolVersion.Major);

    public ConnectorCapabilities Capabilities => ConnectorCapabilities.None;

    public string ConnectionConfigSchema => """
        { "type": "object", "properties": {
            "url": { "type": "string", "format": "uri" },
            "token": { "type": "string" } },
          "additionalProperties": false }
        """;

    public string DatasetConfigSchema => """
        { "type": "object", "properties": {
            "entity": { "type": "string" },
            "per_page": { "type": "integer", "minimum": 1, "maximum": 100 },
            "ref": { "type": "string" } },
          "additionalProperties": false }
        """;

    public ValueTask<ValidationResult> ValidateAsync(ConnectorConfig config, CancellationToken ct)
    {
        var errors = new List<string>();
        GithubConnectionConfig.Parse(config, errors);
        return ValueTask.FromResult(errors.Count == 0 ? ValidationResult.Success : new ValidationResult(errors));
    }

    public async ValueTask<ConnectionCheck> CheckConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        var errors = new List<string>();
        var connection = GithubConnectionConfig.Parse(config, errors);
        if (connection is null)
        {
            return new ConnectionCheck(false, string.Join("; ", errors));
        }

        try
        {
            using var client = GithubHttpClientFactory.Create(connection);

            using var rateLimitResponse = await client.GetAsync("rate_limit", ct).ConfigureAwait(false);
            var rateLimitBody = await rateLimitResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!rateLimitResponse.IsSuccessStatusCode)
            {
                return new ConnectionCheck(false, connection.Redactor.Redact(
                    $"GET /rate_limit returned HTTP {(int)rateLimitResponse.StatusCode}: {rateLimitBody}"));
            }

            using var rateLimitDoc = JsonDocument.Parse(rateLimitBody);
            var rate = rateLimitDoc.RootElement.GetProperty("rate");
            var remaining = rate.GetProperty("remaining").GetInt64();
            var limit = rate.GetProperty("limit").GetInt64();

            if (connection.Token is null)
            {
                return new ConnectionCheck(true, $"unauthenticated, {remaining}/{limit} requests remaining");
            }

            using var userResponse = await client.GetAsync("user", ct).ConfigureAwait(false);
            var userBody = await userResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!userResponse.IsSuccessStatusCode)
            {
                return new ConnectionCheck(false, connection.Redactor.Redact(
                    $"GET /user returned HTTP {(int)userResponse.StatusCode}: {userBody}"));
            }

            using var userDoc = JsonDocument.Parse(userBody);
            var login = userDoc.RootElement.GetProperty("login").GetString();

            return new ConnectionCheck(true, $"authenticated as {login}, {remaining}/{limit} requests remaining");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Every failure is a failed probe, never a crash: a refused connection, a non-JSON body,
            // a missing field, or HttpClient's own request-timeout TaskCanceledException (a subclass
            // of OperationCanceledException that is NOT the caller's cancellation) -- all become a
            // failed ConnectionCheck. Only genuine caller cancellation (ct.IsCancellationRequested)
            // still propagates.
            return new ConnectionCheck(false, connection.Redactor.Redact(ex.Message));
        }
    }

    ValueTask<ISource> ISourceConnector.OpenAsync(ConnectorConfig config, CancellationToken ct)
    {
        var errors = new List<string>();
        var connection = GithubConnectionConfig.Parse(config, errors)
            ?? throw new PzConnectorException($"github: invalid connection config: {string.Join("; ", errors)}", isTransient: false);
        var client = GithubHttpClientFactory.Create(connection);
        return ValueTask.FromResult<ISource>(new GithubSource(connection, client, _loggerFactory.CreateLogger<GithubSource>()));
    }
}
