using SampleApp.Auth;
using SampleApp.Interfaces;
using SampleApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<IUserService, UserService>();
builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<IAuthService, AuthService>();

var app = builder.Build();
app.MapControllers();
app.Run();
