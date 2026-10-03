using FbrInvoicingApp.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FbrInvoicingApp.Web.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/company-access")]
public sealed class CompanyAccessController(InvoiceService invoiceService) : ControllerBase
{
    public sealed record GrantAccessRequest(string Email);

    [HttpPost]
    public async Task<IActionResult> GrantAccess([FromBody] GrantAccessRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await invoiceService.GrantCurrentCompanyAccessAsync(request.Email, cancellationToken);
            return Ok(new { succeeded = true, email = request.Email });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { succeeded = false, error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { succeeded = false, error = ex.Message });
        }
    }
}
