// Q5 probe: the secret comes from configuration, which is empty when the build-time OpenAPI generation starts the host.
using Microsoft.EntityFrameworkCore;
using Pinotechrd.Security.Entities;
using Pinotechrd.Security.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

var secret = builder.Configuration["Pinotechrd:Security:Jwt:Secret"];
#if PINOTECHRD_GUARD
// Only while the document is generated: a random key that is never used to sign anything and never leaves the process.
if (string.IsNullOrEmpty(secret)
    && string.Equals(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name, "GetDocument.Insider", StringComparison.Ordinal))
{
    secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
}
#endif

builder.Services.AddDbContext<ProbeDbContext>();
builder.Services.AddPinotechrdSecurity<ProbeDbContext, ProbeUser, ProbeRole>(o => o.Jwt.Secret = secret!);

var app = builder.Build();
app.MapOpenApi();
app.MapGet("/api/ping", () => "pong");
app.Run();

public sealed class ProbeUser : BaseSecurityUser;

public sealed class ProbeRole : BaseSecurityRole;

public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options);
