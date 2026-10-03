using FbrInvoicingApp.Web.Domain;

namespace FbrInvoicingApp.Web.Data;

public class AppUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "CompanyUser";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ResetToken { get; set; }
    public DateTime? ResetTokenExpiresUtc { get; set; }
    public ICollection<CompanyUser> CompanyUsers { get; set; } = new List<CompanyUser>();
}

