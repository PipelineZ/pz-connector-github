using Pz.Connectors.TestKit;

namespace Pz.Connector.Github.Tests;

/// <summary>The TestKit's credential shapes through this connector's redactor, seeded with the same
/// synthetic secret the suite embeds, exactly as a real config seeds it with the token.</summary>
public sealed class GithubRedactionContract : ErrorRedactionContractTests
{
    protected override string RedactErrorText(string thirdPartyMessage) =>
        new GithubRedactor(["pz-testkit-secret-value"]).Redact(thirdPartyMessage);
}
