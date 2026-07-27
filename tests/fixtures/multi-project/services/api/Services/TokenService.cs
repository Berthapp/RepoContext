namespace SampleApi.Controllers;

/// <summary>Issues and validates API session tokens.</summary>
public sealed class TokenService
{
    private int _issued;

    /// <summary>Whether the service is below its issuing quota.</summary>
    public bool CanIssue() => _issued < 1000;

    /// <summary>Issues a token for the given subject.</summary>
    public string Issue(string subject)
    {
        _issued++;
        return $"api_{subject}_{_issued}";
    }
}
