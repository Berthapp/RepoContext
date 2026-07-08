using SampleApp.Models;

namespace SampleApp.Interfaces;

/// <summary>Authenticates users and issues tokens.</summary>
public interface IAuthService
{
    /// <summary>Authenticates an email/password pair, returning the user on success.</summary>
    User? Authenticate(string email, string password);
}
