using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FbrInvoicingApp.Web.Data;
using FbrInvoicingApp.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FbrInvoicingApp.Web.Controllers;

[ApiController, Route("api/auth")]
public sealed class AuthController(ApplicationDbContext db, PasswordService passwords, JwtService jwtService, RecoveryEmail recovery, ILogger<AuthController> logger) : ControllerBase
{
	[HttpPost("login")]
	public async Task<IActionResult> Login(LoginRequest request)
	{
		var user = await db.AppUsers.Where(x => x.Email == request.Email.ToLower() && x.IsActive).SingleOrDefaultAsync();
		if (user is null || !passwords.Verify(request.Password, user.PasswordHash)) return Unauthorized(new { error = "Invalid email or password." });
		var token = jwtService.CreateToken(user);
		Response.Cookies.Append("fbr_access_token", token, new CookieOptions { HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.AddHours(1) });
		return Ok(new { accessToken = token });
	}

	[HttpPost("login-form")]
	[ValidateAntiForgeryToken]
	public async Task<IActionResult> LoginForm([FromForm] LoginRequest request, [FromForm] string? returnUrl)
	{
		var user = await db.AppUsers.Where(x => x.Email == request.Email.ToLower() && x.IsActive).SingleOrDefaultAsync();
		if (user is null || !passwords.Verify(request.Password, user.PasswordHash)) return Redirect("/Account/Login?error=Invalid%20email%20or%20password.");
		Response.Cookies.Append("fbr_access_token", jwtService.CreateToken(user), new CookieOptions { HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.AddHours(1) });
		return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
	}

	[HttpPost("register-form")]
	[ValidateAntiForgeryToken]
	public async Task<IActionResult> RegisterForm([FromForm] LoginRequest request)
	{
		var email = request.Email.Trim().ToLowerInvariant();
		if (!System.Net.Mail.MailAddress.TryCreate(email, out _) || request.Password.Length < 12)
			return Redirect("/Account/Register?error=Enter%20a%20valid%20email%20and%20a%20password%20of%20at%20least%2012%20characters.");

		if (await db.AppUsers.AnyAsync(x => x.Email == email))
			return Redirect("/Account/Register?error=An%20account%20already%20exists.");

		db.AppUsers.Add(new AppUser { Email = email, PasswordHash = passwords.Hash(request.Password) });

		await db.SaveChangesAsync();

		return Redirect("/Account/Login");
	}

	[HttpPost("forgot-form")]
	[ValidateAntiForgeryToken]
	public async Task<IActionResult> ForgotForm([FromForm] string email)
	{
		var user = await db.AppUsers.SingleOrDefaultAsync(x => x.Email == email.Trim().ToLowerInvariant());
		if (user is not null)
		{
			var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
			user.ResetToken = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token)));
			user.ResetTokenExpiresUtc = DateTime.UtcNow.AddMinutes(30);
			await db.SaveChangesAsync();
			try { await recovery.SendAsync(user.Email, token); }
			catch (Exception ex) { logger.LogError(ex, "Password recovery email delivery failed."); }
		}
		return Redirect("/Account/ForgotPassword?sent=true");
	}

	[HttpPost("reset-form")]
	[ValidateAntiForgeryToken]
	public async Task<IActionResult> ResetForm([FromForm] string token, [FromForm] string password)
	{
		if (password.Length < 12) return Redirect("/Account/ForgotPassword?error=Password%20must%20contain%20at%20least%2012%20characters.");
		var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token)));
		var user = await db.AppUsers.SingleOrDefaultAsync(x => x.ResetToken == hash && x.ResetTokenExpiresUtc > DateTime.UtcNow && x.IsActive);
		if (user is null) return Redirect("/Account/ForgotPassword?error=This%20reset%20link%20is%20invalid%20or%20expired.");
		user.PasswordHash = passwords.Hash(password);
		user.ResetToken = null;
		user.ResetTokenExpiresUtc = null;
		await db.SaveChangesAsync();
		return Redirect("/Account/Login");
	}

	[HttpGet("logout")]
	public IActionResult Logout() { Response.Cookies.Delete("fbr_access_token"); return Redirect("/Account/Login"); }
}

public sealed record LoginRequest(string Email, string Password);
