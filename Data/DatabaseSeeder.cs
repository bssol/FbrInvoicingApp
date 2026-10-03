using Microsoft.EntityFrameworkCore;
using FbrInvoicingApp.Web.Services;

namespace FbrInvoicingApp.Web.Data;

public static class DatabaseSeeder
{
    public static async Task SeedUsersAsync(
        ApplicationDbContext db,
        PasswordService passwords,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("SeedUsers:Enabled", false))
        {
            logger.LogDebug("Initial user seeding is disabled.");
            return;
        }

        var users = new[]
        {
            ReadUser(configuration, "Admin", "Admin"),
            ReadUser(configuration, "Client", "Client")
        };

        foreach (var seedUser in users)
        {
            var existingUser = await db.AppUsers
                .SingleOrDefaultAsync(x => x.Email == seedUser.Email, cancellationToken);

            if (existingUser is not null)
            {
                logger.LogDebug("Seed user {Email} already exists; leaving it unchanged.", seedUser.Email);
                continue;
            }

            db.AppUsers.Add(new AppUser
            {
                Email = seedUser.Email,
                PasswordHash = passwords.Hash(seedUser.Password),
                Role = seedUser.Role,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            });

            logger.LogInformation("Created initial {Role} user {Email}.", seedUser.Role, seedUser.Email);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static SeedUser ReadUser(IConfiguration configuration, string sectionName, string role)
    {
        var email = configuration[$"SeedUsers:{sectionName}:Email"]?.Trim().ToLowerInvariant();
        var password = configuration[$"SeedUsers:{sectionName}:Password"];

        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException($"SeedUsers:{sectionName}:Email must be configured when user seeding is enabled.");

        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new InvalidOperationException($"SeedUsers:{sectionName}:Password must be at least 12 characters when user seeding is enabled.");

        return new SeedUser(email, password, role);
    }

    private sealed record SeedUser(string Email, string Password, string Role);
}
