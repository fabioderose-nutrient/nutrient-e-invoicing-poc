using System.Text;
using static System.Net.WebUtility;

namespace EInvoicing;

/// <summary>The human-readable side of the hybrid invoice: an A4 HTML page that GdPicture converts to PDF.</summary>
public static class InvoiceHtml
{
    public static string Render(Invoice inv, InvoiceTotals t)
    {
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<style>
  @page { size: A4; margin: 0; }
  * { box-sizing: border-box; }
  body { margin: 0; padding: 92px 52px 40px; font-family: "Helvetica Neue", Helvetica, Arial, sans-serif; font-size: 9.5pt; color: #1f2328; }
  .top { display: flex; justify-content: space-between; align-items: flex-start; }
  .seller-name { font-size: 15pt; font-weight: 700; margin-bottom: 4px; }
  .muted { color: #57606a; }
  h1 { font-size: 20pt; letter-spacing: 1px; margin: 0 0 6px; text-align: right; font-weight: 600; }
  table.meta { border-collapse: collapse; margin-left: auto; }
  table.meta td { padding: 1px 0 1px 14px; text-align: right; }
  table.meta td:first-child { color: #57606a; }
  .parties { display: flex; gap: 24px; margin: 26px 0 18px; }
  .box { flex: 1; border: 1px solid #d0d7de; border-radius: 4px; padding: 10px 12px; }
  .label { font-size: 7.5pt; text-transform: uppercase; letter-spacing: .6px; color: #57606a; margin-bottom: 4px; }
  .name { font-weight: 600; }
  table.lines { width: 100%; border-collapse: collapse; }
  table.lines th { font-size: 7.5pt; text-transform: uppercase; letter-spacing: .5px; color: #57606a; text-align: left; border-bottom: 1.5px solid #1f2328; padding: 6px 6px; }
  table.lines td { border-bottom: 1px solid #d8dee4; padding: 6px 6px; vertical-align: top; }
  .n { text-align: right; white-space: nowrap; }
  .desc { color: #57606a; font-size: 8.5pt; }
  .bottom { display: flex; justify-content: space-between; gap: 28px; margin-top: 16px; }
  table.vat { border-collapse: collapse; font-size: 8.5pt; }
  table.vat th, table.vat td { padding: 3px 8px 3px 0; text-align: right; }
  table.vat th { color: #57606a; font-weight: 400; border-bottom: 1px solid #d0d7de; }
  table.vat td:first-child, table.vat th:first-child { text-align: left; }
  table.totals { border-collapse: collapse; min-width: 250px; }
  table.totals td { padding: 3px 0 3px 18px; text-align: right; }
  table.totals td:first-child { text-align: left; padding-left: 0; color: #57606a; }
  table.totals tr.grand td { border-top: 1.5px solid #1f2328; font-weight: 700; color: #1f2328; padding-top: 6px; }
  table.totals tr.due td { font-weight: 700; font-size: 11pt; color: #1f2328; }
  .pay { margin-top: 22px; border-top: 1px solid #d0d7de; padding-top: 10px; display: flex; gap: 36px; }
  .notes { margin-top: 14px; font-size: 8pt; color: #57606a; }
  .notes p { margin: 2px 0; }
  .footer { margin-top: 18px; font-size: 7.5pt; color: #8c959f; }
</style></head><body>
""");
        var s = inv.Seller;
        sb.Append("<div class=\"top\"><div>");
        sb.Append($"<div class=\"seller-name\">{E(s.Name)}</div>");
        sb.Append($"<div>{E(s.Street)}<br>{E(s.Postcode)} {E(s.City)}, {E(CountryName(s.Country))}</div>");
        if (!string.IsNullOrWhiteSpace(s.VatId)) sb.Append($"<div class=\"muted\">VAT ID {E(s.VatId)}</div>");
        if (!string.IsNullOrWhiteSpace(s.LegalId)) sb.Append($"<div class=\"muted\">Company ID {E(s.LegalId)}</div>");
        if (!string.IsNullOrWhiteSpace(s.ContactEmail)) sb.Append($"<div class=\"muted\">{E(s.ContactEmail)}{(string.IsNullOrWhiteSpace(s.ContactPhone) ? "" : " · " + E(s.ContactPhone))}</div>");
        sb.Append("</div><div>");
        sb.Append($"<h1>{(inv.TypeCode == "381" ? "CREDIT NOTE" : "INVOICE")}</h1><table class=\"meta\">");
        Meta(sb, "Invoice number", inv.Number);
        Meta(sb, "Issue date", Date(inv.IssueDate));
        if (inv.DeliveryDate is { } d) Meta(sb, "Delivery date", Date(d));
        if (inv.DueDate is { } due) Meta(sb, "Due date", Date(due));
        Meta(sb, "Buyer reference", inv.BuyerReference);
        Meta(sb, "Purchase order", inv.OrderReference);
        Meta(sb, "Currency", inv.Currency);
        sb.Append("</table></div></div>");

        var b = inv.Buyer;
        sb.Append("<div class=\"parties\"><div class=\"box\"><div class=\"label\">Bill to</div>");
        sb.Append($"<div class=\"name\">{E(b.Name)}</div><div>{E(b.Street)}<br>{E(b.Postcode)} {E(b.City)}, {E(CountryName(b.Country))}</div>");
        if (!string.IsNullOrWhiteSpace(b.VatId)) sb.Append($"<div class=\"muted\">VAT ID {E(b.VatId)}</div>");
        if (!string.IsNullOrWhiteSpace(b.ContactName)) sb.Append($"<div class=\"muted\">Attn. {E(b.ContactName)}</div>");
        sb.Append("</div><div class=\"box\"><div class=\"label\">Payment</div>");
        if (inv.Payment != null)
        {
            sb.Append($"<div>SEPA credit transfer to<br><span class=\"name\">{E(inv.Payment.Iban)}</span></div>");
            if (!string.IsNullOrWhiteSpace(inv.Payment.Bic)) sb.Append($"<div class=\"muted\">BIC {E(inv.Payment.Bic)}</div>");
        }
        sb.Append($"<div class=\"muted\">Reference {E(inv.Number)}</div>");
        if (!string.IsNullOrWhiteSpace(inv.PaymentTerms)) sb.Append($"<div class=\"muted\">{E(inv.PaymentTerms)}</div>");
        sb.Append("</div></div>");

        sb.Append("<table class=\"lines\"><thead><tr><th>#</th><th>Description</th><th class=\"n\">Qty</th><th>Unit</th><th class=\"n\">Unit price</th><th class=\"n\">VAT</th><th class=\"n\">Net amount</th></tr></thead><tbody>");
        for (int i = 0; i < inv.Lines.Count; i++)
        {
            var l = inv.Lines[i];
            sb.Append($"<tr><td>{E(l.Id ?? (i + 1).ToString())}</td><td><div>{E(l.Name)}</div>");
            var sub = string.Join(" · ", new[] { l.SellerItemId, l.Description }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (sub.Length > 0) sb.Append($"<div class=\"desc\">{E(sub)}</div>");
            sb.Append($"</td><td class=\"n\">{Money.Quantity(l.Quantity)}</td><td>{UnitName(l.Unit)}</td><td class=\"n\">{Money.Display(l.UnitPrice)}</td>");
            sb.Append($"<td class=\"n\">{VatLabel(l.VatCategory, l.VatRate)}</td><td class=\"n\">{Money.Display(t.LineNetAmounts[i])}</td></tr>");
        }
        sb.Append("</tbody></table>");

        sb.Append("<div class=\"bottom\"><div><div class=\"label\">VAT breakdown</div><table class=\"vat\"><tr><th>Category</th><th>Rate</th><th>Taxable amount</th><th>VAT</th></tr>");
        foreach (var v in t.VatBreakdown)
            sb.Append($"<tr><td>{E(CategoryName(v.Category))}</td><td>{Money.Rate(v.Rate)}%</td><td>{Money.Display(v.BasisAmount)}</td><td>{Money.Display(v.TaxAmount)}</td></tr>");
        sb.Append("</table>");
        foreach (var v in t.VatBreakdown.Where(v => !string.IsNullOrWhiteSpace(v.ExemptionReason)))
            sb.Append($"<div class=\"notes\">{E(v.ExemptionReason)}</div>");
        sb.Append("</div><table class=\"totals\">");
        Total(sb, "Total of lines", t.LineTotal, inv.Currency);
        foreach (var ac in inv.AllowancesCharges)
            Total(sb, ac.Reason, ac.IsCharge ? ac.Amount : -ac.Amount, inv.Currency);
        if (t.AllowanceTotal != 0 || t.ChargeTotal != 0) Total(sb, "Total net", t.TaxBasisTotal, inv.Currency);
        Total(sb, "Total VAT", t.TaxTotal, inv.Currency);
        sb.Append($"<tr class=\"grand\"><td>Total incl. VAT</td><td>{Money.Display(t.GrandTotal)} {E(inv.Currency)}</td></tr>");
        if (t.PrepaidAmount != 0) Total(sb, "Already paid", -t.PrepaidAmount, inv.Currency);
        sb.Append($"<tr class=\"due\"><td>Amount due</td><td>{Money.Display(t.DuePayable)} {E(inv.Currency)}</td></tr>");
        sb.Append("</table></div>");

        if (inv.Notes.Count > 0)
        {
            sb.Append("<div class=\"notes\">");
            foreach (var n in inv.Notes) sb.Append($"<p>{E(n.Text)}</p>");
            sb.Append("</div>");
        }
        sb.Append("<div class=\"footer\">Hybrid e-invoice (Factur-X, EN 16931 profile). The structured invoice data is embedded in this PDF/A-3 file as factur-x.xml and is the legally relevant part for automated processing.</div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void Meta(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) sb.Append($"<tr><td>{E(label)}</td><td>{E(value)}</td></tr>");
    }

    private static void Total(StringBuilder sb, string label, decimal amount, string currency) =>
        sb.Append($"<tr><td>{E(label)}</td><td>{Money.Display(amount)} {E(currency)}</td></tr>");

    public static string Date(DateOnly d) => d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static string E(string? s) => HtmlEncode(s ?? "");

    private static string VatLabel(string category, decimal rate) => category == "S" ? $"{Money.Rate(rate)}%" : category;

    public static string CategoryName(string code) => code switch
    {
        "S" => "Standard rate",
        "Z" => "Zero rated",
        "E" => "Exempt",
        "AE" => "Reverse charge",
        "K" => "Intra-EU supply",
        "G" => "Export outside the EU",
        "O" => "Outside scope of VAT",
        _ => code,
    };

    private static string UnitName(string code) => code switch
    {
        "C62" or "H87" => "pcs",
        "HUR" => "hour",
        "DAY" => "day",
        "MON" => "month",
        "ANN" => "year",
        "LS" => "lump sum",
        "KGM" => "kg",
        "MTR" => "m",
        _ => code,
    };

    public static string CountryName(string code) => code switch
    {
        "FR" => "France",
        "DE" => "Germany",
        "BE" => "Belgium",
        "NL" => "Netherlands",
        "LU" => "Luxembourg",
        "AT" => "Austria",
        "IT" => "Italy",
        "ES" => "Spain",
        _ => code,
    };
}
