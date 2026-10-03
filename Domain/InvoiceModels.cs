using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FbrInvoicingApp.Web.Domain;

public enum InvoiceStage { Draft = 1, Validated = 2, SentToFbr = 3 }
public enum InvoiceState { Pending = 1, Success = 2, Failed = 3 }
public enum FbrEnvironment { Sandbox = 1, Production = 2 }

public sealed class FBRInvoice
{
    [JsonPropertyName("invoiceType")] public string InvoiceType { get; set; } = "Sale Invoice";
    [JsonPropertyName("invoiceDate")] public string InvoiceDate { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd");
    [JsonPropertyName("sellerNTNCNIC")] public string SellerNTNCNIC { get; set; } = "";
    [JsonPropertyName("sellerBusinessName")] public string SellerBusinessName { get; set; } = "";
    [JsonPropertyName("sellerProvince")] public string SellerProvince { get; set; } = "";
    [JsonPropertyName("sellerAddress")] public string SellerAddress { get; set; } = "";
    [JsonPropertyName("buyerNTNCNIC")] public string BuyerNTNCNIC { get; set; } = "";
    [JsonPropertyName("buyerBusinessName")] public string BuyerBusinessName { get; set; } = "";
    [JsonPropertyName("buyerProvince")] public string BuyerProvince { get; set; } = "";
    [JsonPropertyName("buyerAddress")] public string BuyerAddress { get; set; } = "";
    [JsonPropertyName("buyerRegistrationType")] public string BuyerRegistrationType { get; set; } = "";
    [JsonPropertyName("invoiceRefNo")] public string InvoiceRefNo { get; set; } = "";
    [JsonPropertyName("scenarioId")] public string ScenarioId { get; set; } = "";
    [JsonPropertyName("items")] public List<Item> Items { get; set; } = [];
}

public sealed class Item
{
    [JsonPropertyName("hsCode")] public string HsCode { get; set; } = "";
    [JsonPropertyName("productDescription")] public string ProductDescription { get; set; } = "";
    [JsonPropertyName("rate")] public string Rate { get; set; } = "";
    [JsonPropertyName("uoM")] public string UoM { get; set; } = "";
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [JsonPropertyName("totalValues")] public decimal TotalValues { get; set; }
    [JsonPropertyName("valueSalesExcludingST")] public decimal ValueSalesExcludingST { get; set; }
    [JsonPropertyName("fixedNotifiedValueOrRetailPrice")] public decimal FixedNotifiedValueOrRetailPrice { get; set; }
    [JsonPropertyName("salesTaxApplicable")] public decimal SalesTaxApplicable { get; set; }
    [JsonPropertyName("salesTaxWithheldAtSource")] public decimal SalesTaxWithheldAtSource { get; set; }
    [JsonPropertyName("extraTax")] public decimal ExtraTax { get; set; }
    [JsonPropertyName("furtherTax")] public decimal FurtherTax { get; set; }
    [JsonPropertyName("sroScheduleNo")] public string SroScheduleNo { get; set; } = "";
    [JsonPropertyName("fedPayable")] public decimal FedPayable { get; set; }
    [JsonPropertyName("discount")] public decimal Discount { get; set; }
    [JsonPropertyName("saleType")] public string SaleType { get; set; } = "Goods at standard rate (default)";
    [JsonPropertyName("sroItemSerialNo")] public string SroItemSerialNo { get; set; } = "";
}

public sealed class FbrResult
{
    public bool Succeeded { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string RawResponse { get; set; } = "";
}

public sealed class Company
{
    public int Id { get; set; }
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [Required, MaxLength(30)] public string NtnCnic { get; set; } = "";
    [Required, MaxLength(100)] public string Province { get; set; } = "";
    [Required]
    public string Address { get; set; } = "";
    public CompanyApiCredential? ApiCredential { get; set; }
    public ICollection<CompanyUser> Users { get; set; } = new List<CompanyUser>();
    public ICollection<InvoiceRecord> Invoices { get; set; } = new List<InvoiceRecord>();
}

public sealed class CompanyUser
{
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string UserId { get; set; } = "";
    public AppUser User { get; set; } = null!;
}

public sealed class CompanyApiCredential
{
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string ValidationTokenEncrypted { get; set; } = "";
    public string SandboxTokenEncrypted { get; set; } = "";
    public string ProductionTokenEncrypted { get; set; } = "";
    public FbrEnvironment Environment { get; set; } = FbrEnvironment.Sandbox;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class InvoiceRecord
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string CreatedByUserId { get; set; } = "";
    public string InvoiceRefNo { get; set; } = "";
    public FBRInvoice Payload { get; set; } = new();
    public InvoiceStage Stage { get; set; } = InvoiceStage.Draft;
    public InvoiceState State { get; set; } = InvoiceState.Pending;
    public string? ReceivedInvoiceNumber { get; set; }
    public string? FbrResponseMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<FbrSubmissionAttempt> Attempts { get; set; } = new List<FbrSubmissionAttempt>();
}

public sealed class FbrSubmissionAttempt
{
    public long Id { get; set; }
    public int InvoiceId { get; set; }
    public InvoiceRecord Invoice { get; set; } = null!;
    public FbrEnvironment Environment { get; set; }
    public string Operation { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public string ResponseJson { get; set; } = "";
    public int? HttpStatusCode { get; set; }
    public bool Succeeded { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
