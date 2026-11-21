using System.Security.Claims;
using System.Text.Encodings.Web;
using Eskrb.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Eskrb.Api.Auth;

public static class BearerDefaults
{
    public const string Scheme = "Bearer";
}

/// <summary>Validates opaque bearer tokens against the AuthTokens table (hash lookup).</summary>
public class BearerTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0) return AuthenticateResult.NoResult();

        var db = Context.RequestServices.GetRequiredService<EskrbDb>();
        var hash = TokenHasher.Hash(token);
        var now = DateTime.UtcNow;

        var user = await db.AuthTokens
            .Where(t => t.TokenHash == hash
                && t.RevokedAt == null
                && (t.ExpiresAt == null || t.ExpiresAt > now))
            .Select(t => t.User)
            .FirstOrDefaultAsync();

        if (user is null) return AuthenticateResult.Fail("Invalid token");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, user.Role.ToString().ToLowerInvariant()),
        ], Scheme.Name);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
