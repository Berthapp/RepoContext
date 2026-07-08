using SampleApp.Models;
using SampleApp.Services;
using Xunit;

namespace SampleApp.Tests;

/// <summary>Tests for <see cref="UserService"/> (name-convention linked).</summary>
public sealed class UserServiceTests
{
    [Fact]
    public void CreateUser_ReturnsUserWithEmail()
    {
        var service = new UserService();
        User user = service.CreateUser("user@example.com");
        Assert.Equal("user@example.com", user.Email);
    }

    [Fact]
    public void GetUser_ReturnsCreatedUser()
    {
        var service = new UserService();
        User created = service.CreateUser("user@example.com");
        Assert.Equal(created.Id, service.GetUser(created.Id)!.Id);
    }
}
