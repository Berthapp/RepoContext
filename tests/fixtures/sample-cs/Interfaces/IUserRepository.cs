using SampleApp.Models;

namespace SampleApp.Interfaces;

/// <summary>Persistence abstraction for <see cref="User"/> entities.</summary>
public interface IUserRepository
{
    User? Find(string id);

    void Save(User user);
}
