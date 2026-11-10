using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Ticketing.Api.Domain;
using Ticketing.Api.Options;

namespace Ticketing.Api.Services;

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider time)
{
    public string Create(User user)
    {
        var jwt = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Expires = time.GetUtcNow().AddMinutes(jwt.LifetimeMinutes).UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", user.Id.ToString()),
                new Claim("email", user.Email),
                new Claim("role", user.Role.ToString()),
            ]),
        });
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue("sub")
            ?? throw new InvalidOperationException("Authenticated user has no sub claim."));
}
