namespace SampleApp.Auth;

/// <summary>Deterministic, dependency-free password hasher (fixture only).</summary>
public sealed class PasswordHasher
{
    /// <summary>Returns a stable, non-cryptographic hash of the password.</summary>
    public string Hash(string password)
    {
        int hash = 17;
        foreach (char c in password)
        {
            hash = unchecked((hash * 31) + c);
        }

        return $"h{hash:x8}";
    }
}
