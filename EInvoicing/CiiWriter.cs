using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EInvoicing;

/// <summary>
/// Writes the invoice as UN/CEFACT Cross Industry Invoice (CII D16B), the XML syntax of Factur-X and ZUGFeRD 2,
/// with the EN 16931 profile (guideline ID urn:cen.eu:en16931:2017). Element order follows the CII schema.
/// </summary>
public static class CiiWriter
{
    public const string ProfileEn16931 = "urn:cen.eu:en16931:2017";

    public static readonly XNamespace Rsm = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    public static readonly XNamespace Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    public static readonly XNamespace Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";
    public static readonly XNamespace Qdt = "urn:un:unece:uncefact:data:standard:QualifiedDataType:100";

    public static byte[] Write(Invoice inv, InvoiceTotals totals)
    {
        var root = new XElement(Rsm + "CrossIndustryInvoice",
            new XAttribute(XNamespace.Xmlns + "rsm", Rsm),
            new XAttribute(XNamespace.Xmlns + "qdt", Qdt),
            new XAttribute(XNamespace.Xmlns + "ram", Ram),
            new XAttribute(XNamespace.Xmlns + "udt", Udt),

            new XElement(Rsm + "ExchangedDocumentContext",
                new XElement(Ram + "GuidelineSpecifiedDocumentContextParameter",
                    new XElement(Ram + "ID", ProfileEn16931))),

            new XElement(Rsm + "ExchangedDocument",
                new XElement(Ram + "ID", inv.Number),                                   // BT-1
                new XElement(Ram + "TypeCode", inv.TypeCode),                           // BT-3
                new XElement(Ram + "IssueDateTime", Date(inv.IssueDate)),               // BT-2
                inv.Notes.Select(n => new XElement(Ram + "IncludedNote",                // BG-1
                    new XElement(Ram + "Content", n.Text),
                    Opt(Ram + "SubjectCode", n.Subject)))),

            new XElement(Rsm + "SupplyChainTradeTransaction",
                inv.Lines.Select((line, i) => Line(line, i, totals.LineNetAmounts[i])),
                new XElement(Ram + "ApplicableHeaderTradeAgreement",
                    Opt(Ram + "BuyerReference", inv.BuyerReference),                   // BT-10
                    TradeParty("SellerTradeParty", inv.Seller),                         // BG-4
                    TradeParty("BuyerTradeParty", inv.Buyer),                           // BG-7
                    string.IsNullOrWhiteSpace(inv.OrderReference) ? null :
                        new XElement(Ram + "BuyerOrderReferencedDocument",
                            new XElement(Ram + "IssuerAssignedID", inv.OrderReference))), // BT-13
                new XElement(Ram + "ApplicableHeaderTradeDelivery",
                    inv.DeliveryDate is { } delivery
                        ? new XElement(Ram + "ActualDeliverySupplyChainEvent",
                            new XElement(Ram + "OccurrenceDateTime", Date(delivery)))  // BT-72
                        : null),
                HeaderSettlement(inv, totals)));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            doc.Save(writer);
        return ms.ToArray();
    }

    private static XElement Line(InvoiceLine line, int index, decimal netAmount) =>
        new(Ram + "IncludedSupplyChainTradeLineItem",
            new XElement(Ram + "AssociatedDocumentLineDocument",
                new XElement(Ram + "LineID", line.Id ?? (index + 1).ToString())),       // BT-126
            new XElement(Ram + "SpecifiedTradeProduct",
                Opt(Ram + "SellerAssignedID", line.SellerItemId),                       // BT-155
                new XElement(Ram + "Name", line.Name),                                  // BT-153
                Opt(Ram + "Description", line.Description)),                            // BT-154
            new XElement(Ram + "SpecifiedLineTradeAgreement",
                new XElement(Ram + "NetPriceProductTradePrice",
                    new XElement(Ram + "ChargeAmount", Money.Xml(line.UnitPrice)))),   // BT-146
            new XElement(Ram + "SpecifiedLineTradeDelivery",
                new XElement(Ram + "BilledQuantity", new XAttribute("unitCode", line.Unit),
                    Money.Quantity(line.Quantity))),                                    // BT-129, BT-130
            new XElement(Ram + "SpecifiedLineTradeSettlement",
                new XElement(Ram + "ApplicableTradeTax",
                    new XElement(Ram + "TypeCode", "VAT"),
                    new XElement(Ram + "CategoryCode", line.VatCategory),               // BT-151
                    new XElement(Ram + "RateApplicablePercent", Money.Xml(line.VatRate))), // BT-152
                new XElement(Ram + "SpecifiedTradeSettlementLineMonetarySummation",
                    new XElement(Ram + "LineTotalAmount", Money.Xml(netAmount)))));    // BT-131

    private static XElement TradeParty(string element, Party p) =>
        new(Ram + element,
            new XElement(Ram + "Name", p.Name),
            string.IsNullOrWhiteSpace(p.LegalId) ? null :
                new XElement(Ram + "SpecifiedLegalOrganization",
                    new XElement(Ram + "ID", p.LegalIdScheme is { Length: > 0 } s ? new XAttribute("schemeID", s) : null, p.LegalId)),
            string.IsNullOrWhiteSpace(p.ContactName) && string.IsNullOrWhiteSpace(p.ContactPhone) && string.IsNullOrWhiteSpace(p.ContactEmail) ? null :
                new XElement(Ram + "DefinedTradeContact",
                    Opt(Ram + "PersonName", p.ContactName),
                    string.IsNullOrWhiteSpace(p.ContactPhone) ? null :
                        new XElement(Ram + "TelephoneUniversalCommunication", new XElement(Ram + "CompleteNumber", p.ContactPhone)),
                    string.IsNullOrWhiteSpace(p.ContactEmail) ? null :
                        new XElement(Ram + "EmailURIUniversalCommunication", new XElement(Ram + "URIID", p.ContactEmail))),
            new XElement(Ram + "PostalTradeAddress",
                Opt(Ram + "PostcodeCode", p.Postcode),
                Opt(Ram + "LineOne", p.Street),
                Opt(Ram + "CityName", p.City),
                new XElement(Ram + "CountryID", p.Country)),
            string.IsNullOrWhiteSpace(p.ElectronicAddress) ? null :
                new XElement(Ram + "URIUniversalCommunication",
                    new XElement(Ram + "URIID", new XAttribute("schemeID", "EM"), p.ElectronicAddress)),
            string.IsNullOrWhiteSpace(p.VatId) ? null :
                new XElement(Ram + "SpecifiedTaxRegistration",
                    new XElement(Ram + "ID", new XAttribute("schemeID", "VA"), p.VatId)));

    private static XElement HeaderSettlement(Invoice inv, InvoiceTotals t) =>
        new(Ram + "ApplicableHeaderTradeSettlement",
            new XElement(Ram + "PaymentReference", inv.Number),                         // BT-83
            new XElement(Ram + "InvoiceCurrencyCode", inv.Currency),                    // BT-5
            inv.Payment == null ? null :
                new XElement(Ram + "SpecifiedTradeSettlementPaymentMeans",              // BG-16
                    new XElement(Ram + "TypeCode", "58"),                               // SEPA credit transfer
                    new XElement(Ram + "Information", "SEPA credit transfer"),
                    new XElement(Ram + "PayeePartyCreditorFinancialAccount",
                        new XElement(Ram + "IBANID", inv.Payment.Iban.Replace(" ", "")), // BT-84
                        Opt(Ram + "AccountName", inv.Payment.AccountName)),
                    string.IsNullOrWhiteSpace(inv.Payment.Bic) ? null :
                        new XElement(Ram + "PayeeSpecifiedCreditorFinancialInstitution",
                            new XElement(Ram + "BICID", inv.Payment.Bic))),
            t.VatBreakdown.Select(v => new XElement(Ram + "ApplicableTradeTax",           // BG-23
                new XElement(Ram + "CalculatedAmount", Money.Xml(v.TaxAmount)),         // BT-117
                new XElement(Ram + "TypeCode", "VAT"),
                Opt(Ram + "ExemptionReason", v.ExemptionReason),                        // BT-120
                new XElement(Ram + "BasisAmount", Money.Xml(v.BasisAmount)),            // BT-116
                new XElement(Ram + "CategoryCode", v.Category),                         // BT-118
                Opt(Ram + "ExemptionReasonCode", v.ExemptionReasonCode),                // BT-121
                new XElement(Ram + "RateApplicablePercent", Money.Xml(v.Rate)))),       // BT-119
            inv.AllowancesCharges.Select(ac => new XElement(Ram + "SpecifiedTradeAllowanceCharge", // BG-20, BG-21
                new XElement(Ram + "ChargeIndicator", new XElement(Udt + "Indicator", ac.IsCharge ? "true" : "false")),
                new XElement(Ram + "ActualAmount", Money.Xml(ac.Amount)),
                new XElement(Ram + "Reason", ac.Reason),
                new XElement(Ram + "CategoryTradeTax",
                    new XElement(Ram + "TypeCode", "VAT"),
                    new XElement(Ram + "CategoryCode", ac.VatCategory),
                    new XElement(Ram + "RateApplicablePercent", Money.Xml(ac.VatRate))))),
            string.IsNullOrWhiteSpace(inv.PaymentTerms) && inv.DueDate == null ? null :
                new XElement(Ram + "SpecifiedTradePaymentTerms",
                    Opt(Ram + "Description", inv.PaymentTerms),                         // BT-20
                    inv.DueDate is { } due ? new XElement(Ram + "DueDateDateTime", Date(due)) : null), // BT-9
            new XElement(Ram + "SpecifiedTradeSettlementHeaderMonetarySummation",        // BG-22
                new XElement(Ram + "LineTotalAmount", Money.Xml(t.LineTotal)),          // BT-106
                t.ChargeTotal != 0 ? new XElement(Ram + "ChargeTotalAmount", Money.Xml(t.ChargeTotal)) : null,          // BT-108
                t.AllowanceTotal != 0 ? new XElement(Ram + "AllowanceTotalAmount", Money.Xml(t.AllowanceTotal)) : null, // BT-107
                new XElement(Ram + "TaxBasisTotalAmount", Money.Xml(t.TaxBasisTotal)),  // BT-109
                new XElement(Ram + "TaxTotalAmount", new XAttribute("currencyID", inv.Currency), Money.Xml(t.TaxTotal)), // BT-110
                new XElement(Ram + "GrandTotalAmount", Money.Xml(t.GrandTotal)),        // BT-112
                t.PrepaidAmount != 0 ? new XElement(Ram + "TotalPrepaidAmount", Money.Xml(t.PrepaidAmount)) : null,     // BT-113
                new XElement(Ram + "DuePayableAmount", Money.Xml(t.DuePayable))));      // BT-115

    private static XElement Date(DateOnly date) =>
        new(Udt + "DateTimeString", new XAttribute("format", "102"), date.ToString("yyyyMMdd"));

    private static XElement? Opt(XName name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);
}
