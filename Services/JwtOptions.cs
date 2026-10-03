namespace FbrInvoicingApp.Web.Services;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "FbrInvoicingApp";
    public string Audience { get; set; } = "FbrInvoicingApp.Api";
    public string SigningKey { get; set; } = "change-this-development-key-to-a-long-secret";
    public int Minutes { get; set; } = 60;
}
