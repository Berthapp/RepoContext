using SampleApp.Models;

namespace SampleApp.Interfaces;

/// <summary>Service for looking up and creating users.</summary>
public interface IUserService
{
    /// <summary>Gets a user by id, or <c>null</c> if none exists.</summary>
    User? GetUser(string id);

    /// <summary>Creates a new user with the given email.</summary>
    User CreateUser(string email);
}
