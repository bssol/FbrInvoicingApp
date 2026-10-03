using System.Security.Claims;
using FbrInvoicingApp.Web.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace FbrInvoicingApp.Web.Services;

public sealed class CurrentUser(AuthenticationStateProvider authentication, IHttpContextAccessor accessor, IDbContextFactory<ApplicationDbContext> factory)
{
    public async Task<string> GetIdAsync(CancellationToken cancellationToken = default)
    {
        ClaimsPrincipal principal;
        try { principal = (await authentication.GetAuthenticationStateAsync()).User; }
        catch (InvalidOperationException) { principal = accessor.HttpContext?.User ?? new ClaimsPrincipal(); }
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (long.TryParse(principal.FindFirstValue("exp"), out var expires) && DateTimeOffset.FromUnixTimeSeconds(expires) <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Your session has expired. Sign out and sign in again before continuing.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(id) || !await db.AppUsers.AnyAsync(x => x.Id == id && x.IsActive, cancellationToken))
            throw new InvalidOperationException("Your session no longer matches an active account. 1. Sign out. 2. Sign in again. 3. Select your CSV and retry. No invoice data was saved.");
        return id;
    }
}
