using System.Reflection;
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

    public string ConnectionConfigSchema => "{ \"type\": \"object\" }";

    public string DatasetConfigSchema => "{ \"type\": \"object\" }";

    public ValueTask<ValidationResult> ValidateAsync(ConnectorConfig config, CancellationToken ct)
    {
        return ValueTask.FromResult(ValidationResult.Success);
    }

    public ValueTask<ConnectionCheck> CheckConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    ValueTask<ISource> ISourceConnector.OpenAsync(ConnectorConfig config, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
