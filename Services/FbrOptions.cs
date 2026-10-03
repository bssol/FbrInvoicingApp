namespace FbrInvoicingApp.Web.Services;

public sealed class FbrOptions
{
    public string ValidationUrl { get; set; } = "https://gw.fbr.gov.pk/di_data/v1/di/validateinvoicedata_sb";
    public string SandboxUrl { get; set; } = "https://gw.fbr.gov.pk/di_data/v1/di/postinvoicedata_sb";
    public string ProductionUrl { get; set; } = "https://gw.fbr.gov.pk/di_data/v1/di/postinvoicedata";
}
