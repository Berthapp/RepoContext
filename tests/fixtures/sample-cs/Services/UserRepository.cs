using System.Collections.Concurrent;
using SampleApp.Interfaces;
using SampleApp.Models;

namespace SampleApp.Services;

/// <summary>In-memory <see cref="IUserRepository"/>.</summary>
public sealed class UserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<string, User> _store = new();

    public User? Find(string id) =>
        _store.TryGetValue(id, out User? user) ? user : null;

    public void Save(User user) => _store[user.Id] = user;
}
