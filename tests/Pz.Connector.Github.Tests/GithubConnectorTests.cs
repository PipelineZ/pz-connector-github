namespace Pz.Connector.Github.Tests;

public sealed class GithubConnectorTests
{
    [Fact]
    public void Info_name_is_github()
    {
        var connector = new GithubConnector();
        Assert.Equal("github", connector.Info.Name);
    }
}
