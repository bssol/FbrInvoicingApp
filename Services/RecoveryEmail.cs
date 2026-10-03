using System.Net;
using System.Net.Mail;
namespace FbrInvoicingApp.Web.Services;
public sealed class RecoveryEmail(IConfiguration configuration)
{
    public async Task SendAsync(string email, string token)
    {
        var host = configuration["Smtp:Host"];
        var sender = configuration["Smtp:From"];
        var baseUrl = configuration["Smtp:PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(sender) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Email recovery requires Smtp:Host, From and PublicBaseUrl configuration.");
        using var client = new SmtpClient(host, configuration.GetValue("Smtp:Port", 587)) { EnableSsl = true };
        if (!string.IsNullOrWhiteSpace(configuration["Smtp:Username"]))
            client.Credentials = new NetworkCredential(configuration["Smtp:Username"], configuration["Smtp:Password"]);
        using var message = new MailMessage(sender, email, "Reset your Invoice Desk password",
            $"Use this link within 30 minutes: {new Uri(uri, "/Account/ResetPassword")}?token={Uri.EscapeDataString(token)}");
        await client.SendMailAsync(message);
    }
}
