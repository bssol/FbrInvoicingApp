using FbrInvoicingApp.Web.Domain;

namespace FbrInvoicingApp.Web.Services;

public interface IFbrClient
{
    Task<FbrResult> ValidateAsync(FBRInvoice invoice, string token, CancellationToken cancellationToken = default);
    Task<FbrResult> SendAsync(FBRInvoice invoice, string token, FbrEnvironment environment, CancellationToken cancellationToken = default);
}
