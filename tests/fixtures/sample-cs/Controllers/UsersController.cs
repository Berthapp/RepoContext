using Microsoft.AspNetCore.Mvc;
using SampleApp.Interfaces;
using SampleApp.Models;

namespace SampleApp.Controllers;

/// <summary>REST endpoints for users.</summary>
[ApiController]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IUserService _users;

    public UsersController(IUserService users)
    {
        _users = users;
    }

    [HttpGet("{id}")]
    public ActionResult<User> GetById(string id)
    {
        User? user = _users.GetUser(id);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPost]
    public ActionResult<User> Create([FromBody] string email)
    {
        return Ok(_users.CreateUser(email));
    }
}
