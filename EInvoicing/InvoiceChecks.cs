using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EInvoicing;

public enum CheckStatus { Pass, Warn, Fail }

public sealed record CheckResult(string Group, string Rule, string Title, CheckStatus Status, string Detail);

public sealed class InvoiceCheckReport
{
    public ReceivedPdf Pdf { get; init; } = new();
    public ParsedInvoice? Invoice { get; set; }
    public string? PrettyXml { get; set; }
    public List<CheckResult> Checks { get; } = new();
    public int Failed => Checks.Count(c => c.Status == CheckStatus.Fail);
    public int Warnings => Checks.Count(c => c.Status == CheckStatus.Warn);
    public string Verdict => Failed > 0 ? "Rejected" : Warnings > 0 ? "Accepted with warnings" : "Accepted";
}

/// <summary>
/// Incoming side: checks of a received hybrid invoice. Container checks (PDF/A-3, attachment, XMP),
/// required EN 16931 fields, and the arithmetic of the totals (EN 16931 rules BR-CO-10 to BR-CO-17).
/// This is a readable subset of the official validation artefacts, not a replacement for them.
/// </summary>
public static class InvoiceChecks
{
    private const decimal Tolerance = 0.01m;

    public static InvoiceCheckReport Run(ReceivedPdf pdf)
    {
        var report = new InvoiceCheckReport { Pdf = pdf };
        var checks = report.Checks;

        // Container
        bool isA3 = pdf.PdfaConformance.StartsWith("PDF_A_3", StringComparison.Ordinal);
        checks.Add(new("PDF", "PDF/A-3", "The PDF declares PDF/A-3", isA3 ? CheckStatus.Pass : CheckStatus.Fail,
            $"Declared conformance: {Pretty(pdf.PdfaConformance)}. Factur-X and ZUGFeRD require PDF/A-3."));

        bool hasXml = pdf.InvoiceXml != null;
        checks.Add(new("PDF", "Attachment", "The invoice XML is embedded", hasXml ? CheckStatus.Pass : CheckStatus.Fail,
            hasXml ? $"{pdf.InvoiceFileName} ({pdf.InvoiceXml!.Length:N0} bytes). Attachments: {string.Join(", ", pdf.EmbeddedFiles)}."
                   : pdf.EmbeddedFiles.Count == 0 ? "The PDF has no embedded file." : $"No invoice XML among: {string.Join(", ", pdf.EmbeddedFiles)}."));

        var xmp = pdf.Xmp;
        checks.Add(new("PDF", "XMP", "Factur-X / ZUGFeRD XMP metadata", xmp != null ? CheckStatus.Pass : CheckStatus.Fail,
            xmp == null ? "No fx: or zf: properties in the XMP metadata."
                        : $"DocumentType {xmp.DocumentType}, DocumentFileName {xmp.DocumentFileName}, Version {xmp.Version}, ConformanceLevel {xmp.ConformanceLevel}."));
        if (xmp != null && hasXml)
        {
            bool sameName = string.Equals(xmp.DocumentFileName, pdf.InvoiceFileName, StringComparison.OrdinalIgnoreCase);
            checks.Add(new("PDF", "XMP", "XMP file name matches the attachment", sameName ? CheckStatus.Pass : CheckStatus.Fail,
                $"XMP: {xmp.DocumentFileName}, attachment: {pdf.InvoiceFileName}."));
        }
        if (!hasXml) return report;

        // XML syntax and profile
        XDocument doc;
        try
        {
            doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(pdf.InvoiceXml!).TrimStart('﻿'));
            report.PrettyXml = doc.ToString();
        }
        catch (System.Xml.XmlException ex)
        {
            checks.Add(new("Data", "XML", "The XML is well formed", CheckStatus.Fail, ex.Message));
            return report;
        }
        if (!CiiReader.IsCii(doc))
        {
            checks.Add(new("Data", "Syntax", "UN/CEFACT CII D16B", CheckStatus.Fail,
                $"Root element {doc.Root?.Name}. Only CII (Factur-X 1.x, ZUGFeRD 2.x) is checked; ZUGFeRD 1.0 uses an older schema."));
            return report;
        }
        var inv = CiiReader.Parse(doc);
        report.Invoice = inv;
        checks.Add(new("Data", "Syntax", "UN/CEFACT CII D16B", CheckStatus.Pass, "rsm:CrossIndustryInvoice"));

        string? expectedLevel = FacturXReader.ExpectedConformanceLevel(inv.ProfileId);
        checks.Add(new("Data", "BT-24", "Profile", expectedLevel != null ? CheckStatus.Pass : CheckStatus.Warn,
            $"{inv.ProfileId ?? "(missing)"}{(expectedLevel != null ? $" = {expectedLevel}" : ", unknown profile")}."));
        if (xmp?.ConformanceLevel != null && expectedLevel != null)
        {
            bool match = string.Equals(xmp.ConformanceLevel, expectedLevel, StringComparison.OrdinalIgnoreCase);
            checks.Add(new("Data", "XMP", "XMP conformance level matches the XML profile", match ? CheckStatus.Pass : CheckStatus.Fail,
                $"XMP: {xmp.ConformanceLevel}, XML: {expectedLevel}."));
        }

        RequiredFields(inv, checks);
        Arithmetic(inv, checks);
        Consistency(inv, pdf, checks);
        return report;
    }

    private static void RequiredFields(ParsedInvoice inv, List<CheckResult> checks)
    {
        var missing = new List<string>();
        void Need(string? value, string field) { if (string.IsNullOrWhiteSpace(value)) missing.Add(field); }
        Need(inv.Number, "BT-1 invoice number");
        Need(inv.IssueDate?.ToString(), "BT-2 issue date");
        Need(inv.TypeCode, "BT-3 type code");
        Need(inv.Currency, "BT-5 currency");
        Need(inv.Seller.Name, "BT-27 seller name");
        Need(inv.Seller.Country, "BT-40 seller country");
        Need(inv.Buyer.Name, "BT-44 buyer name");
        Need(inv.Buyer.Country, "BT-55 buyer country");
        if (inv.Lines.Count == 0) missing.Add("BG-25 invoice lines");
        if (inv.VatBreakdown.Count == 0) missing.Add("BG-23 VAT breakdown");
        foreach (var (name, value) in new[] { ("BT-106", inv.LineTotal), ("BT-109", inv.TaxBasisTotal), ("BT-112", inv.GrandTotal), ("BT-115", inv.DuePayable) })
            if (value == null) missing.Add($"{name} total");
        for (int i = 0; i < inv.Lines.Count; i++)
        {
            var l = inv.Lines[i];
            string id = l.Id ?? (i + 1).ToString();
            if (string.IsNullOrWhiteSpace(l.Name)) missing.Add($"line {id}: BT-153 item name");
            if (l.Quantity == null) missing.Add($"line {id}: BT-129 quantity");
            if (l.NetPrice == null) missing.Add($"line {id}: BT-146 net price");
            if (l.NetAmount == null) missing.Add($"line {id}: BT-131 net amount");
            if (string.IsNullOrWhiteSpace(l.VatCategory)) missing.Add($"line {id}: BT-151 VAT category");
        }
        checks.Add(new("Required fields", "EN 16931", "Mandatory business terms are present",
            missing.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
            missing.Count == 0 ? "Invoice number, dates, currency, parties, lines, VAT breakdown and totals." : "Missing: " + string.Join(", ", missing) + "."));

        bool sellerId = !string.IsNullOrWhiteSpace(inv.Seller.VatId) || !string.IsNullOrWhiteSpace(inv.Seller.LegalId);
        checks.Add(new("Required fields", "BR-CO-26", "Seller VAT ID or legal registration ID", sellerId ? CheckStatus.Pass : CheckStatus.Fail,
            sellerId ? $"VAT ID {inv.Seller.VatId ?? "-"}, legal ID {inv.Seller.LegalId ?? "-"}." : "Neither BT-31 nor BT-30 is present."));

        bool needsBuyerVat = inv.VatBreakdown.Any(v => v.Category is "AE" or "K");
        if (needsBuyerVat)
            checks.Add(new("Required fields", "BR-AE-02", "Buyer VAT ID for reverse charge / intra-EU supply",
                string.IsNullOrWhiteSpace(inv.Buyer.VatId) ? CheckStatus.Fail : CheckStatus.Pass, $"Buyer VAT ID {inv.Buyer.VatId ?? "missing"}."));

        bool duePositive = (inv.DuePayable ?? 0) > 0;
        bool hasTerms = inv.DueDate != null || !string.IsNullOrWhiteSpace(inv.PaymentTerms);
        checks.Add(new("Required fields", "BR-CO-25", "Due date or payment terms when an amount is due",
            !duePositive || hasTerms ? CheckStatus.Pass : CheckStatus.Fail,
            hasTerms ? $"Due date {(inv.DueDate is { } d ? InvoiceHtml.Date(d) : "-")}. Terms: {inv.PaymentTerms?.TrimEnd('.') ?? "-"}." : "Neither BT-9 nor BT-20 is present."));
    }

    private static void Arithmetic(ParsedInvoice inv, List<CheckResult> checks)
    {
        // Each line: net amount = quantity x net price / base quantity.
        var wrongLines = inv.Lines
            .Where(l => l.Quantity != null && l.NetPrice != null && l.NetAmount != null)
            .Where(l => Math.Abs(Money.Round(l.Quantity!.Value * l.NetPrice!.Value / l.BasisQuantity) - l.NetAmount!.Value) > Tolerance)
            .Select(l => $"line {l.Id}: {Money.Quantity(l.Quantity!.Value)} x {Money.Xml(l.NetPrice!.Value)} = {Money.Xml(Money.Round(l.Quantity!.Value * l.NetPrice!.Value / l.BasisQuantity))}, declared {Money.Xml(l.NetAmount!.Value)}")
            .ToList();
        checks.Add(new("Arithmetic", "Lines", "Line net amount = quantity x net price", wrongLines.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
            wrongLines.Count == 0 ? $"{inv.Lines.Count} lines checked." : string.Join("; ", wrongLines) + "."));

        decimal sumLines = inv.Lines.Sum(l => l.NetAmount ?? 0);
        Compare(checks, "BR-CO-10", "Sum of line net amounts = total of lines (BT-106)", sumLines, inv.LineTotal,
            $"Sum of {inv.Lines.Count} lines");

        decimal allowances = inv.AllowancesCharges.Where(a => !a.IsCharge).Sum(a => a.Amount ?? 0);
        decimal charges = inv.AllowancesCharges.Where(a => a.IsCharge).Sum(a => a.Amount ?? 0);
        if (allowances != 0 || inv.AllowanceTotal != null)
            Compare(checks, "BR-CO-11", "Sum of allowances = BT-107", allowances, inv.AllowanceTotal ?? 0, "Sum of allowances");
        if (charges != 0 || inv.ChargeTotal != null)
            Compare(checks, "BR-CO-12", "Sum of charges = BT-108", charges, inv.ChargeTotal ?? 0, "Sum of charges");

        decimal basis = (inv.LineTotal ?? 0) - (inv.AllowanceTotal ?? 0) + (inv.ChargeTotal ?? 0);
        Compare(checks, "BR-CO-13", "Taxable total (BT-109) = lines - allowances + charges", basis, inv.TaxBasisTotal,
            $"{Money.Xml(inv.LineTotal ?? 0)} - {Money.Xml(inv.AllowanceTotal ?? 0)} + {Money.Xml(inv.ChargeTotal ?? 0)}");

        // VAT breakdown: taxable amount per category and rate, then VAT = taxable amount x rate.
        var basisProblems = new List<string>();
        var rateProblems = new List<string>();
        foreach (var v in inv.VatBreakdown)
        {
            decimal expectedBasis =
                inv.Lines.Where(l => l.VatCategory == v.Category && l.VatRate == v.Rate).Sum(l => l.NetAmount ?? 0)
                - inv.AllowancesCharges.Where(a => !a.IsCharge && a.VatCategory == v.Category && a.VatRate == v.Rate).Sum(a => a.Amount ?? 0)
                + inv.AllowancesCharges.Where(a => a.IsCharge && a.VatCategory == v.Category && a.VatRate == v.Rate).Sum(a => a.Amount ?? 0);
            if (Math.Abs(expectedBasis - v.BasisAmount) > Tolerance)
                basisProblems.Add($"{v.Category} {Money.Rate(v.Rate)}%: lines give {Money.Xml(expectedBasis)}, declared {Money.Xml(v.BasisAmount)}");
            decimal expectedTax = Money.Round(v.BasisAmount * v.Rate / 100m);
            if (Math.Abs(expectedTax - v.TaxAmount) > Tolerance)
                rateProblems.Add($"{v.Category} {Money.Rate(v.Rate)}%: {Money.Xml(v.BasisAmount)} x {Money.Rate(v.Rate)}% = {Money.Xml(expectedTax)}, declared {Money.Xml(v.TaxAmount)}");
        }
        var groupsWithoutRow = inv.Lines.Where(l => l.VatCategory != null)
            .Select(l => (l.VatCategory!, l.VatRate ?? 0)).Distinct()
            .Where(g => !inv.VatBreakdown.Any(v => v.Category == g.Item1 && v.Rate == g.Item2))
            .Select(g => $"{g.Item1} {Money.Rate(g.Item2)}% has lines but no VAT breakdown row").ToList();
        basisProblems.AddRange(groupsWithoutRow);
        checks.Add(new("Arithmetic", "BR-S-08", "VAT breakdown taxable amounts match the lines", basisProblems.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
            basisProblems.Count == 0 ? $"{inv.VatBreakdown.Count} VAT rows: " + string.Join(", ", inv.VatBreakdown.Select(v => $"{v.Category} {Money.Rate(v.Rate)}% on {Money.Xml(v.BasisAmount)}")) + "."
                                     : string.Join("; ", basisProblems) + "."));
        checks.Add(new("Arithmetic", "BR-CO-17", "VAT amount = taxable amount x rate", rateProblems.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
            rateProblems.Count == 0 ? string.Join(", ", inv.VatBreakdown.Select(v => $"{Money.Xml(v.BasisAmount)} x {Money.Rate(v.Rate)}% = {Money.Xml(v.TaxAmount)}")) + "."
                                    : string.Join("; ", rateProblems) + "."));

        Compare(checks, "BR-CO-14", "Total VAT (BT-110) = sum of the VAT breakdown", inv.VatBreakdown.Sum(v => v.TaxAmount), inv.TaxTotal,
            "Sum of VAT rows");
        Compare(checks, "BR-CO-15", "Net + VAT = gross (BT-112 = BT-109 + BT-110)", (inv.TaxBasisTotal ?? 0) + (inv.TaxTotal ?? 0), inv.GrandTotal,
            $"{Money.Xml(inv.TaxBasisTotal ?? 0)} + {Money.Xml(inv.TaxTotal ?? 0)}");
        Compare(checks, "BR-CO-16", "Amount due (BT-115) = gross - prepaid + rounding",
            (inv.GrandTotal ?? 0) - (inv.PrepaidAmount ?? 0) + (inv.RoundingAmount ?? 0), inv.DuePayable,
            $"{Money.Xml(inv.GrandTotal ?? 0)} - {Money.Xml(inv.PrepaidAmount ?? 0)} + {Money.Xml(inv.RoundingAmount ?? 0)}");
    }

    private static void Consistency(ParsedInvoice inv, ReceivedPdf pdf, List<CheckResult> checks)
    {
        if (inv.IssueDate is { } issue && inv.DueDate is { } due)
            checks.Add(new("Consistency", "Dates", "Due date is not before the issue date", due >= issue ? CheckStatus.Pass : CheckStatus.Warn,
                $"Issued {InvoiceHtml.Date(issue)}, due {InvoiceHtml.Date(due)}."));

        // The visible invoice and the XML must tell the same story: look for the number and the amount due in the page text.
        string text = Regex.Replace(pdf.VisibleText, @"\s+", " ");
        var notFound = new List<string>();
        if (inv.Number != null && !text.Contains(inv.Number, StringComparison.Ordinal)) notFound.Add($"invoice number {inv.Number}");
        if (inv.DuePayable is { } amount && !text.Contains(Money.Display(amount)) && !text.Contains(Money.Xml(amount)))
            notFound.Add($"amount due {Money.Display(amount)}");
        if (inv.GrandTotal is { } gross && !text.Contains(Money.Display(gross)) && !text.Contains(Money.Xml(gross)))
            notFound.Add($"gross total {Money.Display(gross)}");
        checks.Add(new("Consistency", "Visual", "The PDF page shows the XML invoice number and totals",
            notFound.Count == 0 ? CheckStatus.Pass : CheckStatus.Warn,
            notFound.Count == 0 ? "Found in the page text extracted with GdPicture." : "Not found on the page: " + string.Join(", ", notFound) + "."));
    }

    private static void Compare(List<CheckResult> checks, string rule, string title, decimal expected, decimal? declared, string how)
    {
        if (declared == null)
        {
            checks.Add(new("Arithmetic", rule, title, CheckStatus.Fail, $"{how} = {Money.Xml(expected)}, the declared value is missing."));
            return;
        }
        bool ok = Math.Abs(expected - declared.Value) <= Tolerance;
        checks.Add(new("Arithmetic", rule, title, ok ? CheckStatus.Pass : CheckStatus.Fail,
            ok ? $"{how} = {Money.Xml(expected)}." : $"{how} = {Money.Xml(expected)}, declared {Money.Xml(declared.Value)} (difference {Money.Xml(declared.Value - expected)})."));
    }

    public static string Pretty(string conformance) =>
        conformance.StartsWith("PDF_A_", StringComparison.Ordinal) ? conformance.Replace("PDF_A_", "PDF/A-") : "none (not PDF/A)";
}
