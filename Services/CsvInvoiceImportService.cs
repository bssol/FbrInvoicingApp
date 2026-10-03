using System.Globalization;
using System.Security.Claims;
using FbrInvoicingApp.Web.Data;
using FbrInvoicingApp.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace FbrInvoicingApp.Web.Services;

public sealed class CsvInvoiceImportService(IDbContextFactory<ApplicationDbContext> factory, CurrentUser user, ILogger<CsvInvoiceImportService> logger)
{
    private static readonly string[] ExpectedColumns =
    [
        "hsCode", "productDescription", "rate", "uoM", "quantity", "totalValues",
        "valueSalesExcludingST", "fixedNotifiedValueOrRetailPrice", "salesTaxApplicable",
        "salesTaxWithheldAtSource", "extraTax", "furtherTax", "sroScheduleNo", "fedPayable",
        "discount", "saleType", "sroItemSerialNo", "invoiceType", "invoiceDate", "sellerNTNCNIC",
        "sellerBusinessName", "sellerProvince", "sellerAddress", "buyerNTNCNIC", "buyerBusinessName",
        "buyerProvince", "buyerAddress", "buyerRegistrationType", "invoiceRefNo", "scenarioId"
    ];

    public async Task<CsvImportResult> ImportAsync(Stream csvStream, string sourceName, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        string userId;
        try { userId = await user.GetIdAsync(cancellationToken); }
        catch (InvalidOperationException ex) { return CsvImportResult.Failed(ex.Message); }
        using var reader = new StreamReader(csvStream);
        var contents = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(contents))
            return CsvImportResult.Failed("The CSV file is empty.");

        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(contents)) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(",");
        List<string> headers;
        try { headers = parser.ReadFields()!.ToList(); }
        catch (Microsoft.VisualBasic.FileIO.MalformedLineException) { return CsvImportResult.Failed("Row 1: malformed CSV header. Check quote pairs and comma separators."); }
        var headerErrors = ValidateHeaders(headers);
        if (headerErrors.Count > 0)
            return new CsvImportResult(0, 0, 0, headerErrors);

        var rows = new List<Dictionary<string, string>>(capacity: 256);
        var errors = new List<string>();
        var lineNumber = 1;

        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber = (int)parser.LineNumber;
            List<string> values;
            try { values = parser.ReadFields()!.ToList(); }
            catch (Microsoft.VisualBasic.FileIO.MalformedLineException) { errors.Add($"Row {lineNumber}: malformed quoted field. Check the opening and closing quotes."); continue; }
            if (values.Count != headers.Count)
            {
                errors.Add($"Row {lineNumber}: expected {headers.Count} columns but found {values.Count}.");
                continue;
            }

            var row = headers.Select((header, index) => new { header, value = values[index].Trim() })
                .ToDictionary(x => x.header, x => x.value, StringComparer.OrdinalIgnoreCase);
            rows.Add(row);
            ValidateRow(row, lineNumber, errors);
        }

        if (rows.Count == 0 && errors.Count == 0)
            errors.Add("The CSV file does not contain any invoice rows.");

        var groups = rows.GroupBy(x => x["invoiceRefNo"], StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var group in groups)
        {
            var first = group.First();
            var headerFields = new[] { "invoiceType", "invoiceDate", "sellerNTNCNIC", "sellerBusinessName", "sellerProvince", "sellerAddress", "buyerNTNCNIC", "buyerBusinessName", "buyerProvince", "buyerAddress", "buyerRegistrationType", "scenarioId" };
            foreach (var field in headerFields)
            {
                if (group.Any(row => !string.Equals(row[field], first[field], StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"Invoice {group.Key}: header field '{field}' differs between line items.");
            }
        }

        if (errors.Count > 0)
            return new CsvImportResult(rows.Count, 0, 0, errors);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var company = await db.Companies.FirstOrDefaultAsync(x => x.Users.Any(user => user.UserId == userId), cancellationToken);
            var companyCreated = false;

            if (rows.Any(x => !string.Equals(x["sellerNTNCNIC"], rows[0]["sellerNTNCNIC"], StringComparison.OrdinalIgnoreCase)))
                return CsvImportResult.Failed("This CSV contains more than one seller. Split it into one file per company and import each file separately.");
            if (company is not null && !string.Equals(company.NtnCnic.Trim(), rows[0]["sellerNTNCNIC"], StringComparison.OrdinalIgnoreCase))
                return CsvImportResult.Failed($"The CSV seller tax number does not match your company ({company.Name}). Check Company setup and select the correct seller file.");

            if (company is null)
            {
                var first = rows[0];
                company = new Company
                {
                    Name = first["sellerBusinessName"],
                    NtnCnic = first["sellerNTNCNIC"],
                    Province = first["sellerProvince"],
                    Address = first["sellerAddress"]
                };
                db.Companies.Add(company);
                await db.SaveChangesAsync(cancellationToken);
                companyCreated = true;
            }

            if (!await db.CompanyUsers.AnyAsync(x => x.CompanyId == company.Id && x.UserId == userId, cancellationToken))
                db.CompanyUsers.Add(new CompanyUser { CompanyId = company.Id, UserId = userId });

            var invoiceReferences = groups.Select(x => x.Key).ToArray();
            var existingReferences = await db.Invoices
                .Where(x => x.CompanyId == company.Id && invoiceReferences.Contains(x.InvoiceRefNo))
                .Select(x => x.InvoiceRefNo)
                .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, cancellationToken);
            var inserted = 0;
            var skippedReferences = new List<string>();

            foreach (var group in groups)
            {
                if (existingReferences.Contains(group.Key))
                {
                    skippedReferences.Add(group.Key);
                    continue;
                }

                var first = group.First();
                var payload = new FBRInvoice
                {
                    InvoiceType = first["invoiceType"],
                    InvoiceDate = ParseDate(first["invoiceDate"]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    SellerNTNCNIC = first["sellerNTNCNIC"],
                    SellerBusinessName = first["sellerBusinessName"],
                    SellerProvince = first["sellerProvince"],
                    SellerAddress = first["sellerAddress"],
                    BuyerNTNCNIC = first["buyerNTNCNIC"],
                    BuyerBusinessName = first["buyerBusinessName"],
                    BuyerProvince = first["buyerProvince"],
                    BuyerAddress = first["buyerAddress"],
                    BuyerRegistrationType = first["buyerRegistrationType"],
                    InvoiceRefNo = group.Key,
                    ScenarioId = first["scenarioId"],
                    Items = group.Select(MapItem).ToList()
                };

                db.Invoices.Add(new InvoiceRecord
                {
                    CompanyId = company.Id,
                    CreatedByUserId = userId,
                    InvoiceRefNo = group.Key,
                    Payload = payload,
                    Stage = InvoiceStage.Draft,
                    State = InvoiceState.Pending,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                inserted++;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CsvImportResult(rows.Count, inserted, skippedReferences.Count, [], company.Id, companyCreated, sourceName, skippedReferences);
        }
        catch (DbUpdateException ex)
        {
            var incident = Guid.NewGuid().ToString("N")[..10];
            logger.LogError(ex, "CSV import failed. Reference {Incident}", incident);
            return CsvImportResult.Failed($"1. File validation completed. 2. Database saving failed and this upload was rolled back. 3. Sign out and sign in again if your account was recreated. Otherwise ask your administrator to check error reference {incident}. No invoice from this upload was committed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var incident = Guid.NewGuid().ToString("N")[..10];
            logger.LogError(ex, "CSV import failed. Reference {Incident}", incident);
            return CsvImportResult.Failed($"1. File validation completed. 2. Database processing failed. No changes from this upload were committed. 3. Retry after checking the database connection or give your administrator error reference {incident}.");
        }
    }

    private static Item MapItem(IReadOnlyDictionary<string, string> row) => new()
    {
        HsCode = row["hsCode"],
        ProductDescription = row["productDescription"],
        Rate = row["rate"],
        UoM = row["uoM"],
        Quantity = ParseDecimal(row["quantity"]),
        TotalValues = ParseDecimal(row["totalValues"]),
        ValueSalesExcludingST = ParseDecimal(row["valueSalesExcludingST"]),
        FixedNotifiedValueOrRetailPrice = ParseDecimal(row["fixedNotifiedValueOrRetailPrice"]),
        SalesTaxApplicable = ParseDecimal(row["salesTaxApplicable"]),
        SalesTaxWithheldAtSource = ParseDecimal(row["salesTaxWithheldAtSource"]),
        ExtraTax = ParseDecimal(row["extraTax"]),
        FurtherTax = ParseDecimal(row["furtherTax"]),
        SroScheduleNo = row["sroScheduleNo"],
        FedPayable = ParseDecimal(row["fedPayable"]),
        Discount = ParseDecimal(row["discount"]),
        SaleType = row["saleType"],
        SroItemSerialNo = row["sroItemSerialNo"]
    };

    private static void ValidateRow(IReadOnlyDictionary<string, string> row, int lineNumber, ICollection<string> errors)
    {
        foreach (var field in new[] { "hsCode", "productDescription", "rate", "uoM", "invoiceType", "invoiceDate", "sellerNTNCNIC", "sellerBusinessName", "sellerProvince", "sellerAddress", "buyerNTNCNIC", "buyerBusinessName", "buyerProvince", "buyerAddress", "buyerRegistrationType", "invoiceRefNo", "scenarioId" })
            if (string.IsNullOrWhiteSpace(row[field])) errors.Add($"Row {lineNumber}: '{field}' is required.");

        if (!DateTime.TryParseExact(row["invoiceDate"], "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            errors.Add($"Row {lineNumber}: invoiceDate must use dd/MM/yyyy.");

        foreach (var field in new[] { "quantity", "totalValues", "valueSalesExcludingST", "fixedNotifiedValueOrRetailPrice", "salesTaxApplicable", "salesTaxWithheldAtSource", "extraTax", "furtherTax", "fedPayable", "discount" })
            if (!decimal.TryParse(row[field], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) || value < 0)
                errors.Add($"Row {lineNumber}: '{field}' must be a non-negative number.");

        var rateText = row["rate"].TrimEnd('%');
        if (!decimal.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
            errors.Add($"Row {lineNumber}: rate must be a percentage such as 18%.");
        else if (decimal.TryParse(row["valueSalesExcludingST"], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var salesValue) && decimal.TryParse(row["salesTaxApplicable"], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var tax) && decimal.Abs(decimal.Round(salesValue * rate / 100m, 2) - tax) > .01m)
            errors.Add($"Row {lineNumber}: sales tax does not match the supplied rate.");

        if (decimal.TryParse(row["totalValues"], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var total) && decimal.TryParse(row["valueSalesExcludingST"], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var valueExcludingTax) && decimal.TryParse(row["salesTaxApplicable"], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var applicableTax) && decimal.Abs(total - valueExcludingTax - applicableTax) > .01m)
            errors.Add($"Row {lineNumber}: totalValues must equal sales value plus sales tax.");
    }

    private static List<string> ValidateHeaders(IReadOnlyList<string> headers)
    {
        var errors = new List<string>();
        foreach (var duplicate in headers.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            errors.Add($"Duplicate column '{duplicate.Key}'. Each column must appear once.");
        if (headers.Count != ExpectedColumns.Length) errors.Add($"Expected {ExpectedColumns.Length} columns but found {headers.Count}.");
        foreach (var column in ExpectedColumns)
            if (!headers.Contains(column, StringComparer.OrdinalIgnoreCase)) errors.Add($"Missing column '{column}'.");
        return errors;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { value.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted) { values.Add(value.ToString()); value.Clear(); }
            else value.Append(character);
        }
        values.Add(value.ToString());
        return values;
    }

    private static DateTime ParseDate(string value) => DateTime.ParseExact(value, "dd/MM/yyyy", CultureInfo.InvariantCulture);
    private static decimal ParseDecimal(string value) => decimal.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
    private static string DescribeDatabaseSaveError(DbUpdateException exception, string sourceName)
    {
        var detail = exception.InnerException?.Message?.Trim() ?? exception.Message;
        if (detail.Contains("IX_Invoices_CompanyId_InvoiceRefNo", StringComparison.OrdinalIgnoreCase))
            return $"Import stopped while saving invoice references from '{sourceName}'. One or more invoice references already exist for this company. The upload was rolled back; no partial data was saved. Please upload only new references, or use the duplicate-skipping result after refreshing the page. Database detail: {detail}";

        if (detail.Contains("CompanyUsers", StringComparison.OrdinalIgnoreCase))
            return $"Import stopped while linking your user account to the company. The upload was rolled back; no partial data was saved. Refresh the page and try again. Database detail: {detail}";

        return $"Import stopped while saving the company or invoice records from '{sourceName}'. The upload was rolled back; no partial data was saved. Check that the company and invoice reference data are valid, then try again. Database detail: {detail}";
    }
}

public sealed record CsvImportResult(int RowsRead, int InvoicesInserted, int ExistingSkipped, IReadOnlyList<string> Errors, int? CompanyId = null, bool CompanyCreated = false, string? SourceName = null, IReadOnlyList<string>? SkippedInvoiceReferences = null)
{
    public bool Succeeded => Errors.Count == 0;
    public static CsvImportResult Failed(string error) => new(0, 0, 0, [error]);
}
