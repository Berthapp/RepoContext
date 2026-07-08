using System.Collections.Concurrent;
using SampleApp.Interfaces;
using SampleApp.Models;

namespace SampleApp.Services;

/// <summary>In-memory <see cref="IUserService"/> implementation.</summary>
public sealed class UserService : IUserService
{
    private readonly ConcurrentDictionary<string, User> _users = new();
    private int _sequence;

    /// <inheritdoc />
    public User? GetUser(string id) =>
        _users.TryGetValue(id, out User? user) ? user : null;

    /// <inheritdoc />
    public User CreateUser(string email)
    {
        string id = $"u{Interlocked.Increment(ref _sequence)}";
        var user = new User { Id = id, Email = email };
        _users[id] = user;
        return user;
    }
}
