using SampleApp.Auth;
using SampleApp.Models;
using SampleApp.Services;
using Xunit;

namespace SampleApp.Tests;

/// <summary>Tests for <see cref="AuthService"/> (name-convention linked).</summary>
public sealed class AuthServiceTests
{
    [Fact]
    public void Authenticate_ReturnsUser_ForAnyCredentials()
    {
        var auth = new AuthService(new UserService(), new PasswordHasher());
        User? user = auth.Authenticate("user@example.com", "secret");
        Assert.NotNull(user);
    }
}
