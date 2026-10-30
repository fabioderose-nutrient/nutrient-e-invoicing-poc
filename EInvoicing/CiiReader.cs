using System.Globalization;
using System.Xml.Linq;

namespace EInvoicing;

/// <summary>Invoice data read back from a CII XML (Factur-X or ZUGFeRD 2.x). Every field is optional so the checks can report what is missing.</summary>
public sealed class ParsedInvoice
{
    public string? ProfileId { get; set; }
    public string? Number { get; set; }
    public string? TypeCode { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateOnly? DeliveryDate { get; set; }
    public string? Currency { get; set; }
    public string? BuyerReference { get; set; }
    public string? OrderReference { get; set; }
    public string? PaymentTerms { get; set; }
    public string? Iban { get; set; }
    public ParsedParty Seller { get; set; } = new();
    public ParsedParty Buyer { get; set; } = new();
    public List<ParsedLine> Lines { get; } = new();
    public List<ParsedAllowanceCharge> AllowancesCharges { get; } = new();
    public List<VatBreakdown> VatBreakdown { get; } = new();
    public decimal? LineTotal { get; set; }
    public decimal? AllowanceTotal { get; set; }
    public decimal? ChargeTotal { get; set; }
    public decimal? TaxBasisTotal { get; set; }
    public decimal? TaxTotal { get; set; }
    public decimal? RoundingAmount { get; set; }
    public decimal? GrandTotal { get; set; }
    public decimal? PrepaidAmount { get; set; }
    public decimal? DuePayable { get; set; }
}

public sealed class ParsedParty
{
    public string? Name { get; set; }
    public string? Street { get; set; }
    public string? Postcode { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? VatId { get; set; }
    public string? LegalId { get; set; }
}

public sealed class ParsedLine
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? NetPrice { get; set; }
    public decimal BasisQuantity { get; set; } = 1m;
    public decimal? NetAmount { get; set; }
    public string? VatCategory { get; set; }
    public decimal? VatRate { get; set; }
}

public sealed class ParsedAllowanceCharge
{
    public bool IsCharge { get; set; }
    public decimal? Amount { get; set; }
    public string? Reason { get; set; }
    public string? VatCategory { get; set; }
    public decimal? VatRate { get; set; }
}

public static class CiiReader
{
    private static readonly XNamespace Rsm = CiiWriter.Rsm;
    private static readonly XNamespace Ram = CiiWriter.Ram;
    private static readonly XNamespace Udt = CiiWriter.Udt;

    /// <summary>True when the XML root is a CII D16B CrossIndustryInvoice (Factur-X 1.x, ZUGFeRD 2.x, XRechnung CII).</summary>
    public static bool IsCii(XDocument doc) => doc.Root?.Name == Rsm + "CrossIndustryInvoice";

    public static ParsedInvoice Parse(XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidDataException("Empty XML.");
        var context = root.Element(Rsm + "ExchangedDocumentContext");
        var header = root.Element(Rsm + "ExchangedDocument");
        var tx = root.Element(Rsm + "SupplyChainTradeTransaction");
        var agreement = tx?.Element(Ram + "ApplicableHeaderTradeAgreement");
        var delivery = tx?.Element(Ram + "ApplicableHeaderTradeDelivery");
        var settlement = tx?.Element(Ram + "ApplicableHeaderTradeSettlement");
        var sums = settlement?.Element(Ram + "SpecifiedTradeSettlementHeaderMonetarySummation");
        var terms = settlement?.Element(Ram + "SpecifiedTradePaymentTerms");

        var inv = new ParsedInvoice
        {
            ProfileId = Text(context, "GuidelineSpecifiedDocumentContextParameter", "ID"),
            Number = Text(header, "ID"),
            TypeCode = Text(header, "TypeCode"),
            IssueDate = DateValue(header?.Element(Ram + "IssueDateTime")),
            Currency = Text(settlement, "InvoiceCurrencyCode"),
            BuyerReference = Text(agreement, "BuyerReference"),
            OrderReference = Text(agreement, "BuyerOrderReferencedDocument", "IssuerAssignedID"),
            DeliveryDate = DateValue(delivery?.Element(Ram + "ActualDeliverySupplyChainEvent")?.Element(Ram + "OccurrenceDateTime")),
            DueDate = DateValue(terms?.Element(Ram + "DueDateDateTime")),
            PaymentTerms = Text(terms, "Description"),
            Iban = Text(settlement, "SpecifiedTradeSettlementPaymentMeans", "PayeePartyCreditorFinancialAccount", "IBANID"),
            Seller = Party(agreement?.Element(Ram + "SellerTradeParty")),
            Buyer = Party(agreement?.Element(Ram + "BuyerTradeParty")),
            LineTotal = Amount(sums, "LineTotalAmount"),
            AllowanceTotal = Amount(sums, "AllowanceTotalAmount"),
            ChargeTotal = Amount(sums, "ChargeTotalAmount"),
            TaxBasisTotal = Amount(sums, "TaxBasisTotalAmount"),
            RoundingAmount = Amount(sums, "RoundingAmount"),
            GrandTotal = Amount(sums, "GrandTotalAmount"),
            PrepaidAmount = Amount(sums, "TotalPrepaidAmount"),
            DuePayable = Amount(sums, "DuePayableAmount"),
        };

        // BT-110 is the TaxTotalAmount in the invoice currency (a second one may exist in the VAT accounting currency).
        var taxTotals = sums?.Elements(Ram + "TaxTotalAmount").ToList() ?? new();
        var taxTotal = taxTotals.FirstOrDefault(e => (string?)e.Attribute("currencyID") == inv.Currency) ?? taxTotals.FirstOrDefault();
        inv.TaxTotal = Dec(taxTotal?.Value);

        foreach (var item in tx?.Elements(Ram + "IncludedSupplyChainTradeLineItem") ?? Enumerable.Empty<XElement>())
        {
            var price = item.Element(Ram + "SpecifiedLineTradeAgreement")?.Element(Ram + "NetPriceProductTradePrice");
            var qty = item.Element(Ram + "SpecifiedLineTradeDelivery")?.Element(Ram + "BilledQuantity");
            var lineSettlement = item.Element(Ram + "SpecifiedLineTradeSettlement");
            inv.Lines.Add(new ParsedLine
            {
                Id = Text(item, "AssociatedDocumentLineDocument", "LineID"),
                Name = Text(item, "SpecifiedTradeProduct", "Name"),
                Quantity = Dec(qty?.Value),
                Unit = (string?)qty?.Attribute("unitCode"),
                NetPrice = Amount(price, "ChargeAmount"),
                BasisQuantity = Dec(price?.Element(Ram + "BasisQuantity")?.Value) is { } b && b != 0 ? b : 1m,
                NetAmount = Amount(lineSettlement?.Element(Ram + "SpecifiedTradeSettlementLineMonetarySummation"), "LineTotalAmount"),
                VatCategory = Text(lineSettlement, "ApplicableTradeTax", "CategoryCode"),
                VatRate = Dec(Text(lineSettlement, "ApplicableTradeTax", "RateApplicablePercent")),
            });
        }

        foreach (var ac in settlement?.Elements(Ram + "SpecifiedTradeAllowanceCharge") ?? Enumerable.Empty<XElement>())
        {
            inv.AllowancesCharges.Add(new ParsedAllowanceCharge
            {
                IsCharge = string.Equals(ac.Element(Ram + "ChargeIndicator")?.Element(Udt + "Indicator")?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
                Amount = Amount(ac, "ActualAmount"),
                Reason = Text(ac, "Reason"),
                VatCategory = Text(ac, "CategoryTradeTax", "CategoryCode"),
                VatRate = Dec(Text(ac, "CategoryTradeTax", "RateApplicablePercent")),
            });
        }

        foreach (var tax in settlement?.Elements(Ram + "ApplicableTradeTax") ?? Enumerable.Empty<XElement>())
        {
            inv.VatBreakdown.Add(new VatBreakdown
            {
                Category = Text(tax, "CategoryCode") ?? "",
                Rate = Dec(Text(tax, "RateApplicablePercent")) ?? 0m,
                BasisAmount = Amount(tax, "BasisAmount") ?? 0m,
                TaxAmount = Amount(tax, "CalculatedAmount") ?? 0m,
                ExemptionReason = Text(tax, "ExemptionReason"),
                ExemptionReasonCode = Text(tax, "ExemptionReasonCode"),
            });
        }
        return inv;
    }

    private static ParsedParty Party(XElement? p) => new()
    {
        Name = Text(p, "Name"),
        Street = Text(p, "PostalTradeAddress", "LineOne"),
        Postcode = Text(p, "PostalTradeAddress", "PostcodeCode"),
        City = Text(p, "PostalTradeAddress", "CityName"),
        Country = Text(p, "PostalTradeAddress", "CountryID"),
        VatId = p?.Elements(Ram + "SpecifiedTaxRegistration").Select(e => e.Element(Ram + "ID"))
                  .FirstOrDefault(id => (string?)id?.Attribute("schemeID") == "VA")?.Value.Trim(),
        LegalId = Text(p, "SpecifiedLegalOrganization", "ID"),
    };

    private static string? Text(XElement? parent, params string[] path)
    {
        var current = parent;
        foreach (var name in path)
        {
            current = current?.Element(Ram + name);
            if (current == null) return null;
        }
        var value = current?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static decimal? Amount(XElement? parent, string name) => Dec(parent?.Element(Ram + name)?.Value);

    private static decimal? Dec(string? value) =>
        decimal.TryParse(value?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateOnly? DateValue(XElement? dateTime)
    {
        var s = dateTime?.Element(Udt + "DateTimeString")?.Value.Trim();
        return s != null && DateOnly.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
