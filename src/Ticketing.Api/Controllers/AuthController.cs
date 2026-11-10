using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Services;

namespace Ticketing.Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting(AuthRateLimit.Policy)]
public sealed class AuthController(TicketingDbContext db, TokenService tokens) : ControllerBase
{
    private static readonly PasswordHasher<User> Hasher = new();
    private static readonly User UnknownUser = new() { Email = "" };
    private static readonly string UnknownUserHash = Hasher.HashPassword(UnknownUser, Guid.NewGuid().ToString());

    [HttpPost("register")]
    public async Task<ActionResult<TokenResponse>> Register(CredentialsRequest request, CancellationToken ct)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = request.Email.Trim().ToLowerInvariant(),
            Role = UserRole.Customer,
        };
        user.PasswordHash = Hasher.HashPassword(user, request.Password);
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (TicketingDbContext.IsUniqueViolation(ex))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Email is already registered.");
        }

        return Ok(new TokenResponse(tokens.Create(user)));
    }

    [HttpPost("login")]
    public async Task<ActionResult<TokenResponse>> Login(CredentialsRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email, ct);

        // Hash even when the email is unknown, so response time doesn't reveal who has an account.
        var verified = Hasher.VerifyHashedPassword(
            user ?? UnknownUser, user?.PasswordHash ?? UnknownUserHash, request.Password);

        if (user is null || verified == PasswordVerificationResult.Failed)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid email or password.");
        }

        return Ok(new TokenResponse(tokens.Create(user)));
    }
}
