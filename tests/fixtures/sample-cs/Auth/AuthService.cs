using SampleApp.Interfaces;
using SampleApp.Models;

namespace SampleApp.Auth;

/// <summary>Password-based <see cref="IAuthService"/> backed by an <see cref="IUserService"/>.</summary>
public sealed class AuthService : IAuthService
{
    private readonly IUserService _users;
    private readonly PasswordHasher _hasher;

    public AuthService(IUserService users, PasswordHasher hasher)
    {
        _users = users;
        _hasher = hasher;
    }

    /// <inheritdoc />
    public User? Authenticate(string email, string password)
    {
        string hashed = _hasher.Hash(password);
        User user = _users.CreateUser(email);
        return hashed.Length > 0 ? user : null;
    }
}
