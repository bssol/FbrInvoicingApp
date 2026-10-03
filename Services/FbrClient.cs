using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FbrInvoicingApp.Web.Domain;
using Microsoft.Extensions.Options;

namespace FbrInvoicingApp.Web.Services;

public sealed class FbrClient(HttpClient httpClient, IOptions<FbrOptions> options, ILogger<FbrClient> logger) : IFbrClient
{
    private readonly FbrOptions settings = options.Value;

    public Task<FbrResult> ValidateAsync(FBRInvoice invoice, string token, CancellationToken cancellationToken = default) =>
        PostAsync(invoice, token, settings.ValidationUrl, cancellationToken);

    public async Task<FbrResult> SendAsync(FBRInvoice invoice, string token, FbrEnvironment environment, CancellationToken cancellationToken = default)
    {
        var result = await PostAsync(invoice, token, environment == FbrEnvironment.Production ? settings.ProductionUrl : settings.SandboxUrl, cancellationToken);
        if (result.Succeeded && string.IsNullOrWhiteSpace(result.InvoiceNumber))
        { result.Succeeded = false; result.Error = "FBR did not return an invoice number. Reconcile the submission with FBR."; }
        return result;
    }

    private async Task<FbrResult> PostAsync(FBRInvoice invoice, string token, string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Save the required FBR token in Company setup first.");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(invoice), Encoding.UTF8, "application/json");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = Parse(raw, response.IsSuccessStatusCode);
            result.RawResponse = raw;
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "FBR request failed");
            return new FbrResult { Error = ex.Message, Status = "FBR request failed" };
        }
    }

    private static FbrResult Parse(string raw, bool transportSuccess)
    {
        try
        {
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            string? error = null;
            string? invoiceNumber = null;
            if (root.TryGetProperty("invoiceNumber", out var number)) invoiceNumber = number.GetString();
            if (root.TryGetProperty("validationResponse", out var validation))
            {
                if (validation.TryGetProperty("invoiceStatuses", out var lines) && lines.ValueKind == JsonValueKind.Array)
                {
                    var failures = lines.EnumerateArray().Where(x => x.TryGetProperty("statusCode", out var code) && code.GetString() != "00")
                        .Select(x => x.ToString()).ToArray();
                    if (failures.Length > 0) return new FbrResult { Succeeded = false, Error = string.Join("; ", failures), InvoiceNumber = invoiceNumber };
                }
                if (validation.TryGetProperty("statusCode", out var statusCode) && statusCode.GetString() != "00")
                    return new FbrResult { Succeeded = false, Error = validation.ToString(), InvoiceNumber = invoiceNumber };
                if (validation.TryGetProperty("error", out var errorElement)) error = errorElement.GetString();
                if (validation.TryGetProperty("status", out var status))
                    return new FbrResult { Succeeded = transportSuccess && string.IsNullOrWhiteSpace(error) && string.Equals(status.GetString(), "Valid", StringComparison.OrdinalIgnoreCase), Status = status.GetString(), Error = error, InvoiceNumber = invoiceNumber };
            }
            return new FbrResult { Succeeded = transportSuccess && !string.IsNullOrWhiteSpace(invoiceNumber), Error = string.IsNullOrWhiteSpace(invoiceNumber) ? "FBR returned no recognized validation result or invoice number." : error, InvoiceNumber = invoiceNumber };
        }
        catch (JsonException)
        {
            return new FbrResult { Succeeded = false, Status = "Unrecognized response", Error = "FBR returned an invalid JSON response. Review the submission history." };
        }
    }
}
