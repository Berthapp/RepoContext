namespace SampleApi.Controllers;

/// <summary>Liveness endpoint for the API service.</summary>
public sealed class HealthController
{
    private readonly TokenService _tokens;

    public HealthController(TokenService tokens) => _tokens = tokens;

    /// <summary>Reports whether the service can still issue tokens.</summary>
    public string GetHealth() => _tokens.CanIssue() ? "healthy" : "degraded";
}
