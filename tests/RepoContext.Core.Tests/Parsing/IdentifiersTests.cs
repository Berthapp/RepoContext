using RepoContext.Core.Parsing;

namespace RepoContext.Core.Tests.Parsing;

public class IdentifiersTests
{
    [Theory]
    [InlineData("loginUser", new[] { "login", "user" })]
    [InlineData("checkPermission", new[] { "check", "permission" })]
    [InlineData("create_session", new[] { "create", "session" })]
    [InlineData("APIController", new[] { "api", "controller" })]
    [InlineData("IUserService", new[] { "i", "user", "service" })]
    [InlineData("parseJSON2Object", new[] { "parse", "json", "2", "object" })]
    [InlineData("simple", new[] { "simple" })]
    public void Split_ProducesExpectedTokens(string identifier, string[] expected)
    {
        Assert.Equal(expected, Identifiers.Split(identifier));
    }

    [Fact]
    public void Split_EnablesLoginMatchesLoginUser()
    {
        Assert.Contains("login", Identifiers.Split("loginUser"));
    }
}
