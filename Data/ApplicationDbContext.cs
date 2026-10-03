using Microsoft.EntityFrameworkCore;
using FbrInvoicingApp.Web.Domain;

namespace FbrInvoicingApp.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyUser> CompanyUsers => Set<CompanyUser>();
    public DbSet<CompanyApiCredential> CompanyApiCredentials => Set<CompanyApiCredential>();
    public DbSet<InvoiceRecord> Invoices => Set<InvoiceRecord>();
    public DbSet<FbrSubmissionAttempt> FbrSubmissionAttempts => Set<FbrSubmissionAttempt>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<AppUser>().HasKey(x => x.Id);
        builder.Entity<AppUser>().HasIndex(x => x.Email).IsUnique();
        builder.Entity<CompanyUser>().HasKey(x => new { x.CompanyId, x.UserId });
        builder.Entity<CompanyUser>().HasOne(x => x.Company).WithMany(x => x.Users).HasForeignKey(x => x.CompanyId);
        builder.Entity<CompanyUser>().HasOne(x => x.User).WithMany(x => x.CompanyUsers).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CompanyApiCredential>().HasKey(x => x.CompanyId);
        builder.Entity<InvoiceRecord>().Property(x => x.RowVersion).IsRowVersion();
        builder.Entity<InvoiceRecord>().Property(x => x.Payload).HasConversion(
            value => System.Text.Json.JsonSerializer.Serialize(value, (System.Text.Json.JsonSerializerOptions?)null),
            value => System.Text.Json.JsonSerializer.Deserialize<FBRInvoice>(value, (System.Text.Json.JsonSerializerOptions?)null) ?? new FBRInvoice());
        builder.Entity<InvoiceRecord>().HasIndex(x => new { x.CompanyId, x.InvoiceRefNo }).IsUnique();
    }
}
