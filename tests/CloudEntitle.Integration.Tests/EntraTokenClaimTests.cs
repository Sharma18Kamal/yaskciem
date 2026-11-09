using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class EntraTokenClaimTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalClaims_PreserveTenantUserAndDelegatedOrApplicationAccess(bool applicationToken)
    {
        var tenant = Guid.NewGuid().ToString();
        var user = Guid.NewGuid().ToString();
        var audience = Guid.NewGuid().ToString();
        var issuer = $"https://login.microsoftonline.com/{tenant}/v2.0";
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        var claims = new List<Claim> { new("tid", tenant), new("oid", user) };
        claims.Add(applicationToken ? new("roles", "Ciem.Api.Access") : new("scp", "access_as_user"));
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        });
        var options = new JwtBearerOptions { MapInboundClaims = false };
        var handler = Assert.IsType<JsonWebTokenHandler>(Assert.Single(options.TokenHandlers));
        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = key
        });

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(tenant, result.ClaimsIdentity.FindFirst("tid")?.Value);
        Assert.Equal(user, result.ClaimsIdentity.FindFirst("oid")?.Value);
        Assert.Equal(applicationToken ? "Ciem.Api.Access" : "access_as_user",
            result.ClaimsIdentity.FindFirst(applicationToken ? "roles" : "scp")?.Value);

        var rejected = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = Guid.NewGuid().ToString(),
            IssuerSigningKey = key
        });
        Assert.False(rejected.IsValid);
        Assert.IsType<SecurityTokenInvalidAudienceException>(rejected.Exception);
    }
}
