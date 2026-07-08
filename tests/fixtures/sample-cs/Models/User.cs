namespace SampleApp.Models;

/// <summary>Represents an application user.</summary>
public sealed class User
{
    public required string Id { get; init; }

    public required string Email { get; init; }

    public bool IsAdmin { get; init; }
}
