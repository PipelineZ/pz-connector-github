using Microsoft.Extensions.Logging;
using Pz.Connector.Github;
using Pz.Connectors.Sdk;

return await PzConnectorHost.RunAsync(args, ctx => new GithubConnector(ctx.LoggerFactory)).ConfigureAwait(false);
