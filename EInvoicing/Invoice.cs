using System.Text.Json;
using System.Text.Json.Serialization;

namespace EInvoicing;

/// <summary>Invoice data as it comes from the billing system (one JSON file per invoice).</summary>
public sealed class Invoice
{
    public string Number { get; set; } = "";
    /// <summary>UNTDID 1001 document type: 380 = commercial invoice, 381 = credit note.</summary>
    public string TypeCode { get; set; } = "380";
    public DateOnly IssueDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateOnly? DeliveryDate { get; set; }
    public string Currency { get; set; } = "EUR";
    /// <summary>BT-10, the reference the buyer asked to quote.</summary>
    public string? BuyerReference { get; set; }
    /// <summary>BT-13, the buyer's purchase order number.</summary>
    public string? OrderReference { get; set; }
    public string? PaymentTerms { get; set; }
    public List<InvoiceNote> Notes { get; set; } = new();
    public Party Seller { get; set; } = new();
    public Party Buyer { get; set; } = new();
    public PaymentAccount? Payment { get; set; }
    public List<InvoiceLine> Lines { get; set; } = new();
    /// <summary>Document-level allowances (discounts) and charges.</summary>
    public List<AllowanceCharge> AllowancesCharges { get; set; } = new();
    /// <summary>VAT exemption reasons, required for categories E, AE, K, G, O.</summary>
    public List<VatExemption> VatExemptions { get; set; } = new();
    public decimal PrepaidAmount { get; set; }
    /// <summary>
    /// Totals as declared by the issuing system. Normally empty: the totals are computed from the lines.
    /// Only used by the incoming sample that simulates a supplier whose system declares wrong totals.
    /// </summary>
    public DeclaredTotals? DeclaredTotals { get; set; }

    public static Invoice Load(string path)
    {
        var invoice = JsonSerializer.Deserialize<Invoice>(File.ReadAllText(path), JsonOptions)
                      ?? throw new InvalidDataException($"{path} is empty.");
        if (string.IsNullOrWhiteSpace(invoice.Number)) throw new InvalidDataException($"{path}: number is missing.");
        if (invoice.Lines.Count == 0) throw new InvalidDataException($"{path}: the invoice has no lines.");
        return invoice;
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}

public sealed class InvoiceNote
{
    /// <summary>UNTDID 4451 subject code, for example AAB, PMT, PMD, REG.</summary>
    public string? Subject { get; set; }
    public string Text { get; set; } = "";
}

public sealed class Party
{
    public string Name { get; set; } = "";
    public string Street { get; set; } = "";
    public string Postcode { get; set; } = "";
    public string City { get; set; } = "";
    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public string Country { get; set; } = "";
    public string? VatId { get; set; }
    /// <summary>Legal registration identifier (BT-30 / BT-47), for example a company register number.</summary>
    public string? LegalId { get; set; }
    /// <summary>ISO 6523 scheme of the legal identifier, for example 0002 for a French SIREN.</summary>
    public string? LegalIdScheme { get; set; }
    /// <summary>Electronic address (BT-34 / BT-49), sent with scheme EM (email).</summary>
    public string? ElectronicAddress { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
}

public sealed class PaymentAccount
{
    public string Iban { get; set; } = "";
    public string? Bic { get; set; }
    public string? AccountName { get; set; }
}

public sealed class InvoiceLine
{
    public string? Id { get; set; }
    public string? SellerItemId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>UN/ECE Recommendation 20 unit code: C62 = piece, HUR = hour, DAY = day.</summary>
    public string Unit { get; set; } = "C62";
    public decimal UnitPrice { get; set; }
    /// <summary>UNCL 5305 VAT category: S standard, Z zero, E exempt, AE reverse charge, K intra-EU supply.</summary>
    public string VatCategory { get; set; } = "S";
    public decimal VatRate { get; set; }
}

public sealed class AllowanceCharge
{
    /// <summary>false = allowance (discount), true = charge.</summary>
    public bool IsCharge { get; set; }
    public string Reason { get; set; } = "";
    public decimal Amount { get; set; }
    public string VatCategory { get; set; } = "S";
    public decimal VatRate { get; set; }
}

public sealed class VatExemption
{
    public string VatCategory { get; set; } = "";
    public string? Reason { get; set; }
    /// <summary>VATEX code, for example VATEX-EU-AE for reverse charge.</summary>
    public string? ReasonCode { get; set; }
}

public sealed class DeclaredTotals
{
    public decimal LineTotal { get; set; }
    public decimal TaxBasisTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal GrandTotal { get; set; }
    public List<VatBreakdown> VatBreakdown { get; set; } = new();
}

/// <summary>One row of the VAT breakdown (BG-23).</summary>
public sealed class VatBreakdown
{
    public string Category { get; set; } = "S";
    public decimal Rate { get; set; }
    public decimal BasisAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public string? ExemptionReason { get; set; }
    public string? ExemptionReasonCode { get; set; }
}

/// <summary>Document totals (BG-22), in the EN 16931 calculation order.</summary>
public sealed class InvoiceTotals
{
    public List<decimal> LineNetAmounts { get; init; } = new();
    public decimal LineTotal { get; init; }          // BT-106
    public decimal AllowanceTotal { get; init; }     // BT-107
    public decimal ChargeTotal { get; init; }        // BT-108
    public decimal TaxBasisTotal { get; init; }      // BT-109
    public decimal TaxTotal { get; init; }           // BT-110
    public decimal GrandTotal { get; init; }         // BT-112
    public decimal PrepaidAmount { get; init; }      // BT-113
    public decimal DuePayable { get; init; }         // BT-115
    public List<VatBreakdown> VatBreakdown { get; init; } = new();
    public bool FromDeclaredValues { get; init; }

    /// <summary>Computes the totals from the lines with the EN 16931 rules (BR-CO-10 to BR-CO-17).</summary>
    public static InvoiceTotals Compute(Invoice invoice)
    {
        var lineNets = invoice.Lines.Select(l => Money.Round(l.Quantity * l.UnitPrice)).ToList();
        decimal lineTotal = lineNets.Sum();
        decimal allowances = invoice.AllowancesCharges.Where(a => !a.IsCharge).Sum(a => Money.Round(a.Amount));
        decimal charges = invoice.AllowancesCharges.Where(a => a.IsCharge).Sum(a => Money.Round(a.Amount));
        decimal taxBasis = lineTotal - allowances + charges;

        // One VAT breakdown row per category and rate: lines, minus allowances, plus charges of that group.
        var groups = new SortedDictionary<(string Category, decimal Rate), decimal>(
            Comparer<(string Category, decimal Rate)>.Create((a, b) =>
            {
                int byRate = b.Rate.CompareTo(a.Rate);
                return byRate != 0 ? byRate : string.CompareOrdinal(a.Category, b.Category);
            }));
        for (int i = 0; i < invoice.Lines.Count; i++)
            Add(groups, (invoice.Lines[i].VatCategory, invoice.Lines[i].VatRate), lineNets[i]);
        foreach (var ac in invoice.AllowancesCharges)
            Add(groups, (ac.VatCategory, ac.VatRate), ac.IsCharge ? Money.Round(ac.Amount) : -Money.Round(ac.Amount));

        var breakdown = groups.Select(g =>
        {
            var exemption = invoice.VatExemptions.FirstOrDefault(e => e.VatCategory == g.Key.Category);
            return new VatBreakdown
            {
                Category = g.Key.Category,
                Rate = g.Key.Rate,
                BasisAmount = g.Value,
                TaxAmount = Money.Round(g.Value * g.Key.Rate / 100m),
                ExemptionReason = exemption?.Reason,
                ExemptionReasonCode = exemption?.ReasonCode,
            };
        }).ToList();

        decimal taxTotal = breakdown.Sum(b => b.TaxAmount);
        decimal grand = taxBasis + taxTotal;
        return new InvoiceTotals
        {
            LineNetAmounts = lineNets,
            LineTotal = lineTotal,
            AllowanceTotal = allowances,
            ChargeTotal = charges,
            TaxBasisTotal = taxBasis,
            TaxTotal = taxTotal,
            GrandTotal = grand,
            PrepaidAmount = invoice.PrepaidAmount,
            DuePayable = grand - invoice.PrepaidAmount,
            VatBreakdown = breakdown,
        };
    }

    /// <summary>Totals to print and send: the declared ones when the issuing system provides them, else the computed ones.</summary>
    public static InvoiceTotals ForIssuing(Invoice invoice)
    {
        var computed = Compute(invoice);
        var declared = invoice.DeclaredTotals;
        if (declared == null) return computed;
        return new InvoiceTotals
        {
            LineNetAmounts = computed.LineNetAmounts,
            LineTotal = declared.LineTotal,
            AllowanceTotal = computed.AllowanceTotal,
            ChargeTotal = computed.ChargeTotal,
            TaxBasisTotal = declared.TaxBasisTotal,
            TaxTotal = declared.TaxTotal,
            GrandTotal = declared.GrandTotal,
            PrepaidAmount = invoice.PrepaidAmount,
            DuePayable = declared.GrandTotal - invoice.PrepaidAmount,
            VatBreakdown = declared.VatBreakdown.Count > 0 ? declared.VatBreakdown : computed.VatBreakdown,
            FromDeclaredValues = true,
        };
    }

    private static void Add(SortedDictionary<(string, decimal), decimal> groups, (string, decimal) key, decimal amount)
        => groups[key] = groups.TryGetValue(key, out var current) ? current + amount : amount;
}

public static class Money
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Amount as written in the XML: dot decimal separator, two decimals.</summary>
    public static string Xml(decimal value) => Round(value).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Amount as printed on the invoice: thousands separator, two decimals.</summary>
    public static string Display(decimal value) => Round(value).ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);

    public static string Quantity(decimal value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    public static string Rate(decimal value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
