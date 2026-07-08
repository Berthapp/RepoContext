using Microsoft.AspNetCore.Mvc;
using SampleApp.Interfaces;
using SampleApp.Models;

namespace SampleApp.Controllers;

/// <summary>Authentication endpoints.</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth)
    {
        _auth = auth;
    }

    [HttpPost("login")]
    public ActionResult<User> Login([FromBody] LoginRequest request)
    {
        User? user = _auth.Authenticate(request.Email, request.Password);
        return user is null ? Unauthorized() : Ok(user);
    }
}

/// <summary>Request body for <see cref="AuthController.Login"/>.</summary>
public sealed record LoginRequest(string Email, string Password);
