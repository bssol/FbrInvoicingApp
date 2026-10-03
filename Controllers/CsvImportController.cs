using FbrInvoicingApp.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FbrInvoicingApp.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/admin/invoices")]
[Route("api/invoices")]
public sealed class CsvImportController(CsvInvoiceImportService importer) : ControllerBase
{
    [HttpPost("import-csv")]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> ImportCsv(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(CsvImportResult.Failed("Select a non-empty CSV file."));

        await using var stream = file.OpenReadStream();
        var result = await importer.ImportAsync(stream, file.FileName, cancellationToken);
        return result.Succeeded ? Ok(result) : BadRequest(result);
    }
}
