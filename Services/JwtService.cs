using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FbrInvoicingApp.Web.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FbrInvoicingApp.Web.Services;

public sealed class JwtService(IOptions<JwtOptions> options)
{
    public string CreateToken(AppUser user)
    {
        var settings = options.Value;
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, user.Id), new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Name, user.Email), new Claim(ClaimTypes.Role, user.Role), new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")) };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey));
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience, claims, expires: DateTime.UtcNow.AddMinutes(settings.Minutes), signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
