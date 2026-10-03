using System.Security.Claims;
using System.Text.Json;
using FbrInvoicingApp.Web.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FbrInvoicingApp.Web.Services;

public sealed class InvoiceService(IDbContextFactory<ApplicationDbContext> factory, IFbrClient fbrClient, IDataProtectionProvider protection, CurrentUser user)
{
    private readonly IDataProtector tokenProtector = protection.CreateProtector("fbr-api-tokens-v1");

    public async Task<IReadOnlyList<InvoiceRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var userId = await user.GetIdAsync(cancellationToken);
        return await db.Invoices.Where(x => x.Company.Users.Any(u => u.UserId == userId)).OrderByDescending(x => x.CreatedAtUtc).AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<Company?> GetCurrentCompanyAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var userId = await user.GetIdAsync(cancellationToken);
        return await db.Companies
            .AsNoTracking()
            .Where(x => x.Users.Any(user => user.UserId == userId))
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<InvoiceRecord> SaveDraftAsync(InvoiceRecord invoice, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (invoice.Stage == InvoiceStage.SentToFbr) throw new InvalidOperationException("Sent invoices are immutable.");
        var userId = await user.GetIdAsync(cancellationToken);
        if (invoice.CompanyId == 0) invoice.CompanyId = (await GetCurrentCompanyAsync(cancellationToken))?.Id ?? throw new InvalidOperationException("Set up your company first.");
        await EnsureCompanyAccessAsync(invoice.CompanyId, cancellationToken);
        if (string.IsNullOrWhiteSpace(invoice.Payload.InvoiceRefNo) || invoice.Payload.Items.Count == 0) throw new InvalidOperationException("Enter a reference and at least one line item.");
        if (invoice.Payload.Items.Any(x => x.Quantity <= 0 || string.IsNullOrWhiteSpace(x.ProductDescription))) throw new InvalidOperationException("Each line needs a description and a positive quantity.");
        if (invoice.Id != 0)
        {
            var stored = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == invoice.Id, cancellationToken);
            if (stored.CompanyId != invoice.CompanyId) throw new UnauthorizedAccessException();
            EnsureEditable(stored);
        }
        if (invoice.Id == 0) invoice.CreatedByUserId = userId;
        if (!DateOnly.TryParseExact(invoice.Payload.InvoiceDate, "yyyy-MM-dd", out _)) throw new InvalidOperationException("Enter a valid invoice date.");
        if (string.IsNullOrWhiteSpace(invoice.Payload.SellerNTNCNIC) || string.IsNullOrWhiteSpace(invoice.Payload.SellerBusinessName) || string.IsNullOrWhiteSpace(invoice.Payload.BuyerBusinessName))
            throw new InvalidOperationException("Complete the seller tax number and both business names.");
        foreach (var line in invoice.Payload.Items)
        {
            if (string.IsNullOrWhiteSpace(line.HsCode) || string.IsNullOrWhiteSpace(line.UoM) || string.IsNullOrWhiteSpace(line.Rate))
                throw new InvalidOperationException("Each line requires an HS code, unit and tax rate.");
            if (new[] { line.TotalValues, line.ValueSalesExcludingST, line.SalesTaxApplicable, line.FixedNotifiedValueOrRetailPrice, line.SalesTaxWithheldAtSource, line.ExtraTax, line.FurtherTax, line.FedPayable, line.Discount }.Any(x => x < 0))
                throw new InvalidOperationException("Line amounts cannot be negative.");
        }
        invoice.InvoiceRefNo = invoice.Payload.InvoiceRefNo.Trim();
        if (await db.Invoices.AnyAsync(x => x.CompanyId == invoice.CompanyId && x.InvoiceRefNo == invoice.InvoiceRefNo && x.Id != invoice.Id, cancellationToken)) throw new InvalidOperationException("This invoice reference already exists.");
        invoice.Payload.InvoiceRefNo = invoice.InvoiceRefNo;
        invoice.Stage = InvoiceStage.Draft;
        invoice.State = InvoiceState.Pending;
        invoice.FbrResponseMessage = null;
        invoice.UpdatedAtUtc = DateTime.UtcNow;
        if (invoice.Id == 0) db.Invoices.Add(invoice); else db.Invoices.Update(invoice);
        await db.SaveChangesAsync(cancellationToken);
        return invoice;
    }

    public async Task<FbrResult> ValidateAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var invoice = await db.Invoices.SingleAsync(x => x.Id == id, cancellationToken);
        await EnsureCompanyAccessAsync(invoice.CompanyId, cancellationToken);
        EnsureEditable(invoice);
        var credential = await GetCredentialAsync(invoice.CompanyId, cancellationToken);
        var result = await fbrClient.ValidateAsync(invoice.Payload, tokenProtector.Unprotect(credential.ValidationTokenEncrypted), cancellationToken);
        invoice.Stage = result.Succeeded ? InvoiceStage.Validated : InvoiceStage.Draft;
        invoice.State = result.Succeeded ? InvoiceState.Pending : InvoiceState.Failed;
        invoice.FbrResponseMessage = result.Error ?? result.Status;
        invoice.UpdatedAtUtc = DateTime.UtcNow;
        db.FbrSubmissionAttempts.Add(new FbrSubmissionAttempt { InvoiceId = id, Environment = FbrEnvironment.Sandbox, Operation = "Validate", RequestJson = JsonSerializer.Serialize(invoice.Payload), ResponseJson = result.RawResponse, Succeeded = result.Succeeded });
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<FbrResult> SendAsync(int id, FbrEnvironment environment, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var invoice = await db.Invoices.SingleAsync(x => x.Id == id, cancellationToken);
        await EnsureCompanyAccessAsync(invoice.CompanyId, cancellationToken);
        if (invoice.Stage == InvoiceStage.SentToFbr) throw new InvalidOperationException("This invoice has already been sent and cannot be changed.");
        if (invoice.Stage != InvoiceStage.Validated) throw new InvalidOperationException("Invoice must pass FBR validation first.");
        var credential = await GetCredentialAsync(invoice.CompanyId, cancellationToken);
        var encryptedToken = environment == FbrEnvironment.Production ? credential.ProductionTokenEncrypted : credential.SandboxTokenEncrypted;
        if (!Enum.IsDefined(environment)) throw new InvalidOperationException("Choose an FBR environment.");
        var sendToken = tokenProtector.Unprotect(encryptedToken);
        if (string.IsNullOrWhiteSpace(sendToken)) throw new InvalidOperationException("Save the selected environment token in Company setup.");
        invoice.Stage = InvoiceStage.SentToFbr;
        invoice.SentAtUtc = DateTime.UtcNow;
        invoice.FbrResponseMessage = "Submission started; if interrupted, reconcile with FBR before any further action.";
        await db.SaveChangesAsync(cancellationToken);
        var result = await fbrClient.SendAsync(invoice.Payload, tokenProtector.Unprotect(encryptedToken), environment, cancellationToken);
        invoice.Stage = InvoiceStage.SentToFbr;
        invoice.State = result.Succeeded ? InvoiceState.Success : InvoiceState.Failed;
        invoice.ReceivedInvoiceNumber = result.InvoiceNumber;
        invoice.FbrResponseMessage = result.Error ?? result.Status;
        invoice.SentAtUtc = DateTime.UtcNow;
        invoice.UpdatedAtUtc = DateTime.UtcNow;
        db.FbrSubmissionAttempts.Add(new FbrSubmissionAttempt { InvoiceId = id, Environment = environment, Operation = "Send", RequestJson = JsonSerializer.Serialize(invoice.Payload), ResponseJson = result.RawResponse, Succeeded = result.Succeeded });
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task SaveCompanyAsync(Company company, string validationToken, string sandboxToken, string productionToken, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var userId = await user.GetIdAsync(cancellationToken);
        var companyName = company.Name?.Trim() ?? "";
        var ntnCnic = company.NtnCnic?.Trim() ?? "";
        var province = company.Province?.Trim() ?? "";
        var address = company.Address?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(companyName)) throw new InvalidOperationException("Company name is required.");
        if (string.IsNullOrWhiteSpace(ntnCnic)) throw new InvalidOperationException("NTN/CNIC is required.");
        if (string.IsNullOrWhiteSpace(province)) throw new InvalidOperationException("Province is required.");
        if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("Address is required.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var companyEntity = company.Id == 0
            ? await db.Companies.FirstOrDefaultAsync(x => x.Users.Any(user => user.UserId == userId), cancellationToken)
            : await db.Companies.SingleOrDefaultAsync(x => x.Id == company.Id && x.Users.Any(user => user.UserId == userId), cancellationToken);

        if (companyEntity is null)
        {
            if (company.Id != 0)
                throw new UnauthorizedAccessException("You do not have access to this company.");

            companyEntity = new Company();
            db.Companies.Add(companyEntity);
        }

        companyEntity.Name = companyName;
        companyEntity.NtnCnic = ntnCnic;
        companyEntity.Province = province;
        companyEntity.Address = address;
        await db.SaveChangesAsync(cancellationToken);

        if (!await db.CompanyUsers.AnyAsync(x => x.CompanyId == companyEntity.Id && x.UserId == userId, cancellationToken))
            db.CompanyUsers.Add(new CompanyUser { CompanyId = companyEntity.Id, UserId = userId });

        var credential = await db.CompanyApiCredentials
            .SingleOrDefaultAsync(x => x.CompanyId == companyEntity.Id, cancellationToken);

        if (credential is null)
        {
            credential = new CompanyApiCredential
            {
                CompanyId = companyEntity.Id,
                ValidationTokenEncrypted = tokenProtector.Protect(validationToken.Trim()),
                SandboxTokenEncrypted = tokenProtector.Protect(sandboxToken.Trim()),
                ProductionTokenEncrypted = tokenProtector.Protect(productionToken.Trim())
            };
            db.CompanyApiCredentials.Add(credential);
        }
        else
        {
            // Existing credentials are intentionally not displayed back in the form.
            // Blank fields therefore mean "keep the current token".
            if (!string.IsNullOrWhiteSpace(validationToken)) credential.ValidationTokenEncrypted = tokenProtector.Protect(validationToken.Trim());
            if (!string.IsNullOrWhiteSpace(sandboxToken)) credential.SandboxTokenEncrypted = tokenProtector.Protect(sandboxToken.Trim());
            if (!string.IsNullOrWhiteSpace(productionToken)) credential.ProductionTokenEncrypted = tokenProtector.Protect(productionToken.Trim());
        }

        credential.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task GrantCurrentCompanyAccessAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var userId = await user.GetIdAsync(cancellationToken);
        var currentUser = await db.AppUsers.SingleAsync(x => x.Id == userId, cancellationToken);
        if (!string.Equals(currentUser.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only an administrator can grant company access.");

        var company = await db.Companies
            .Where(x => x.Users.Any(user => user.UserId == userId))
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Set up a company before granting access.");

        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedEmail))
            throw new InvalidOperationException("Enter the user's email address.");

        var targetUser = await db.AppUsers.SingleOrDefaultAsync(x => x.Email == normalizedEmail && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("No active user was found with that email address.");

        if (!await db.CompanyUsers.AnyAsync(x => x.CompanyId == company.Id && x.UserId == targetUser.Id, cancellationToken))
        {
            db.CompanyUsers.Add(new CompanyUser { CompanyId = company.Id, UserId = targetUser.Id });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<InvoiceRecord> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var uid = await user.GetIdAsync(cancellationToken);
        return await db.Invoices.AsNoTracking().Include(x => x.Attempts).SingleOrDefaultAsync(x => x.Id == id && x.Company.Users.Any(u => u.UserId == uid), cancellationToken)
            ?? throw new InvalidOperationException("Invoice not found or access denied.");
    }
    public async Task DeleteAsync(int id)
    {
        var invoice = await GetAsync(id);
        EnsureEditable(invoice);
        await using var db = await factory.CreateDbContextAsync();
        db.Invoices.Remove(invoice);
        await db.SaveChangesAsync();
    }
    private async Task<CompanyApiCredential> GetCredentialAsync(int companyId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.CompanyApiCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId, cancellationToken)
            ?? throw new InvalidOperationException("Save your FBR API tokens in Company setup first.");
    }
    private static void EnsureEditable(InvoiceRecord invoice)
    {
        if (invoice.Stage == InvoiceStage.SentToFbr || invoice.SentAtUtc.HasValue) throw new InvalidOperationException("Submitted invoices are locked and cannot be changed or sent again.");
    }
    private async Task EnsureCompanyAccessAsync(int companyId, CancellationToken cancellationToken)
    {
        var uid = await user.GetIdAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.CompanyUsers.AnyAsync(x => x.CompanyId == companyId && x.UserId == uid, cancellationToken)) throw new UnauthorizedAccessException("You do not have access to this company.");
    }
}
