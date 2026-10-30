using System.Text;
using static System.Net.WebUtility;

namespace EInvoicing;

/// <summary>A self-contained HTML report of the received invoices: page image, extracted data, checks and the embedded XML.</summary>
public static class ReportHtml
{
    public static void Write(string path, IReadOnlyList<InvoiceCheckReport> reports)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>E-invoice check report</title>
<style>
  :root { --fg:#1f2328; --muted:#59636e; --line:#d1d9e0; --bg:#f6f8fa; --ok:#1a7f37; --okbg:#dafbe1; --warn:#9a6700; --warnbg:#fff8c5; --bad:#cf222e; --badbg:#ffebe9; }
  * { box-sizing: border-box; }
  body { margin: 0; font: 14px/1.45 -apple-system, "Segoe UI", Helvetica, Arial, sans-serif; color: var(--fg); background: var(--bg); }
  header { padding: 18px 28px; background: #fff; border-bottom: 1px solid var(--line); }
  h1 { font-size: 20px; margin: 0; } h2 { font-size: 17px; margin: 0; } h3 { font-size: 13px; text-transform: uppercase; letter-spacing: .5px; color: var(--muted); margin: 18px 0 6px; }
  main { padding: 20px 28px; max-width: 1400px; }
  table { border-collapse: collapse; width: 100%; }
  th, td { text-align: left; padding: 5px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
  th { font-size: 12px; color: var(--muted); font-weight: 600; }
  .n { text-align: right; white-space: nowrap; }
  .card { background: #fff; border: 1px solid var(--line); border-radius: 6px; padding: 18px 20px; margin-bottom: 22px; }
  .card-head { display: flex; justify-content: space-between; align-items: center; gap: 12px; border-bottom: 1px solid var(--line); padding-bottom: 10px; }
  .grid { display: grid; grid-template-columns: minmax(300px, 520px) 1fr; gap: 28px; margin-top: 14px; }
  .page img { width: 100%; border: 1px solid var(--line); box-shadow: 0 1px 4px rgba(0,0,0,.08); }
  .muted { color: var(--muted); }
  .badge { display: inline-block; padding: 2px 10px; border-radius: 12px; font-size: 12px; font-weight: 600; white-space: nowrap; }
  .Pass, .Accepted { color: var(--ok); background: var(--okbg); }
  .Warn, .Accepted-with-warnings { color: var(--warn); background: var(--warnbg); }
  .Fail, .Rejected { color: var(--bad); background: var(--badbg); }
  .kv { display: grid; grid-template-columns: 150px 1fr; gap: 3px 12px; }
  .kv div:nth-child(odd) { color: var(--muted); }
  tr.fail td { background: #fff5f5; }
  table.checks td { padding: 4px 8px; }
  td.rule { white-space: nowrap; font-size: 12px; color: var(--muted); }
  .detail { font-size: 12px; color: var(--muted); }
  pre { background: #f6f8fa; border: 1px solid var(--line); border-radius: 6px; padding: 10px 12px; font-size: 11.5px; line-height: 1.4; max-height: 640px; overflow: auto; margin: 0; }
  details summary { cursor: pointer; color: var(--muted); margin: 14px 0 6px; font-weight: 600; }
  .xml-tag { color: #0550ae; } .xml-attr { color: #953800; }
  @media (max-width: 900px) { .grid { grid-template-columns: 1fr; } }
</style></head><body>
<header><h1>E-invoice check report</h1><div class="muted">Factur-X / ZUGFeRD hybrid invoices read with GdPicture.NET</div></header><main>
""");
        sb.Append("<div class=\"card\"><h2>Summary</h2><table style=\"margin-top:10px\"><tr><th>File</th><th>Invoice</th><th>Seller</th><th>Buyer</th><th>Profile</th><th class=\"n\">Gross</th><th>Checks</th><th>Verdict</th></tr>");
        foreach (var r in reports)
        {
            var inv = r.Invoice;
            sb.Append($"<tr><td><a href=\"#{Id(r)}\">{E(Path.GetFileName(r.Pdf.Path))}</a></td><td>{E(inv?.Number)}</td><td>{E(inv?.Seller.Name)}</td><td>{E(inv?.Buyer.Name)}</td>");
            sb.Append($"<td>{E(r.Pdf.Xmp?.ConformanceLevel)}</td><td class=\"n\">{(inv?.GrandTotal is { } g ? Money.Display(g) + " " + E(inv.Currency) : "")}</td>");
            sb.Append($"<td>{r.Checks.Count(c => c.Status == CheckStatus.Pass)} passed, {r.Failed} failed, {r.Warnings} warnings</td><td>{Badge(r.Verdict)}</td></tr>");
        }
        sb.Append("</table></div>");

        foreach (var r in reports) Card(sb, r, folder);
        sb.Append("</main></body></html>");
        File.WriteAllText(path, sb.ToString());
    }

    private static void Card(StringBuilder sb, InvoiceCheckReport r, string folder)
    {
        var inv = r.Invoice;
        sb.Append($"<section class=\"card\" id=\"{Id(r)}\"><div class=\"card-head\"><div><h2>{E(Path.GetFileName(r.Pdf.Path))}</h2>");
        sb.Append($"<div class=\"muted\">{E(InvoiceChecks.Pretty(r.Pdf.PdfaConformance))} · {r.Pdf.Pages} page(s) · attachment {E(r.Pdf.InvoiceFileName ?? "none")} · XMP {E(r.Pdf.Xmp?.DocumentType)} {E(r.Pdf.Xmp?.ConformanceLevel)}</div></div>{Badge(r.Verdict)}</div>");
        sb.Append("<div class=\"grid\"><div class=\"page\">");
        if (r.Pdf.PageImagePath != null)
            sb.Append($"<img alt=\"Page 1\" src=\"{E(Path.GetRelativePath(folder, r.Pdf.PageImagePath).Replace('\\', '/'))}\">");
        if (r.PrettyXml != null)
            sb.Append($"<details open><summary>Embedded {E(r.Pdf.InvoiceFileName)}</summary><pre>{HighlightXml(r.PrettyXml)}</pre></details>");
        sb.Append("</div><div>");
        if (inv != null)
        {
            sb.Append("<h3 style=\"margin-top:0\">Extracted from the XML</h3><div class=\"kv\">");
            KV(sb, "Invoice number", inv.Number);
            KV(sb, "Type", inv.TypeCode == "380" ? "380 commercial invoice" : inv.TypeCode == "381" ? "381 credit note" : inv.TypeCode);
            KV(sb, "Issue date / due date", $"{(inv.IssueDate is { } i ? InvoiceHtml.Date(i) : "-")} / {(inv.DueDate is { } d ? InvoiceHtml.Date(d) : "-")}");
            KV(sb, "Seller", Party(inv.Seller));
            KV(sb, "Buyer", Party(inv.Buyer));
            KV(sb, "Buyer reference / PO", $"{inv.BuyerReference ?? "-"} / {inv.OrderReference ?? "-"}");
            KV(sb, "Pay to", inv.Iban);
            sb.Append("</div>");

            sb.Append("<h3>Lines</h3><table><tr><th>#</th><th>Item</th><th class=\"n\">Qty</th><th class=\"n\">Net price</th><th>VAT</th><th class=\"n\">Net amount</th></tr>");
            foreach (var l in inv.Lines)
                sb.Append($"<tr><td>{E(l.Id)}</td><td>{E(l.Name)}</td><td class=\"n\">{(l.Quantity is { } q ? Money.Quantity(q) + " " + E(l.Unit) : "")}</td><td class=\"n\">{Amount(l.NetPrice)}</td><td>{E(l.VatCategory)} {(l.VatRate is { } vr ? Money.Rate(vr) + "%" : "")}</td><td class=\"n\">{Amount(l.NetAmount)}</td></tr>");
            sb.Append("</table>");

            sb.Append("<h3>Totals</h3><table>");
            TotalRow(sb, "BT-106 Total of lines", inv.LineTotal);
            if (inv.AllowanceTotal != null) TotalRow(sb, "BT-107 Allowances", inv.AllowanceTotal);
            if (inv.ChargeTotal != null) TotalRow(sb, "BT-108 Charges", inv.ChargeTotal);
            TotalRow(sb, "BT-109 Total without VAT", inv.TaxBasisTotal);
            foreach (var v in inv.VatBreakdown)
                sb.Append($"<tr><td class=\"muted\">VAT {E(v.Category)} {Money.Rate(v.Rate)}% on {Money.Display(v.BasisAmount)}{(v.ExemptionReasonCode != null ? " (" + E(v.ExemptionReasonCode) + ")" : "")}</td><td class=\"n muted\">{Money.Display(v.TaxAmount)}</td></tr>");
            TotalRow(sb, "BT-110 Total VAT", inv.TaxTotal);
            TotalRow(sb, "BT-112 Total with VAT", inv.GrandTotal);
            if (inv.PrepaidAmount != null) TotalRow(sb, "BT-113 Paid amount", inv.PrepaidAmount);
            TotalRow(sb, $"BT-115 Amount due ({inv.Currency})", inv.DuePayable);
            sb.Append("</table>");
        }

        sb.Append("<h3>Checks</h3><table class=\"checks\">");
        foreach (var c in r.Checks)
            sb.Append($"<tr{(c.Status == CheckStatus.Fail ? " class=\"fail\"" : "")}><td>{Badge(c.Status.ToString())}</td><td class=\"rule\">{E(c.Rule)}</td><td>{E(c.Title)}<div class=\"detail\">{E(c.Detail)}</div></td></tr>");
        sb.Append("</table></div></div>");
        sb.Append("</section>");
    }

    private static string HighlightXml(string xml)
    {
        string encoded = E(xml);
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, @"(&lt;/?)([\w:.-]+)", "$1<span class=\"xml-tag\">$2</span>");
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, @"([\w:-]+)=(&quot;)", "<span class=\"xml-attr\">$1</span>=$2");
        return encoded;
    }

    private static string Party(ParsedParty p) =>
        string.Join(", ", new[] { p.Name, p.City, p.Country, p.VatId != null ? "VAT " + p.VatId : null }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static void KV(StringBuilder sb, string key, string? value) =>
        sb.Append($"<div>{E(key)}</div><div>{(string.IsNullOrWhiteSpace(value) ? "<span class=\"muted\">-</span>" : E(value))}</div>");

    private static void TotalRow(StringBuilder sb, string label, decimal? amount) =>
        sb.Append($"<tr><td>{E(label)}</td><td class=\"n\">{Amount(amount)}</td></tr>");

    private static string Amount(decimal? v) => v is { } d ? Money.Display(d) : "<span class=\"muted\">missing</span>";

    private static string Badge(string text) => $"<span class=\"badge {text.Replace(' ', '-')}\">{E(text)}</span>";

    private static string Id(InvoiceCheckReport r) => "f-" + FileNames.Safe(Path.GetFileNameWithoutExtension(r.Pdf.Path));

    private static string E(string? s) => HtmlEncode(s ?? "");
}
