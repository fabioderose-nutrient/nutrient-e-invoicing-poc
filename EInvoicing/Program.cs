using System.Globalization;
using GdPicture14;

namespace EInvoicing;

internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "demo";
        var options = Options.Parse(args.Skip(1).ToArray());
        if (command is "-h" or "--help" or "help") { PrintHelp(); return 0; }

        try
        {
            RegisterLicense();
            switch (command)
            {
                case "create":
                    Create(ExpandInputs(options.Inputs.Count > 0 ? options.Inputs : new() { "samples/invoices" }, "*.json"), options.Output);
                    return 0;
                case "embed":
                    Embed(options.Inputs, options.Output);
                    return 0;
                case "read":
                    return Read(ExpandInputs(options.Inputs.Count > 0 ? options.Inputs : new() { options.Output }, "*.pdf"), options.Report ?? Path.Combine(options.Output, "report.html"));
                case "demo":
                    // Outgoing: issue the sample invoices. Incoming: read them back, plus the received samples.
                    var issued = Create(ExpandInputs(new() { "samples/invoices" }, "*.json"), options.Output);
                    var received = issued.Concat(ExpandInputs(new() { "samples/incoming" }, "*.pdf")).ToList();
                    Read(received, options.Report ?? Path.Combine(options.Output, "report.html"));
                    return 0;
                case "make-incoming-samples":
                    // Regenerates samples/incoming/*.pdf from samples/incoming-data/*.json.
                    Create(ExpandInputs(new() { "samples/incoming-data" }, "*.json"), "samples/incoming", writeXml: false);
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown command '{command}'.");
                    PrintHelp();
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static List<string> Create(IReadOnlyList<string> jsonFiles, string outputFolder, bool writeXml = true)
    {
        if (jsonFiles.Count == 0) throw new IOException("No invoice JSON file found.");
        PrepareBrowser();
        Console.WriteLine($"Outgoing: {jsonFiles.Count} invoice(s) to Factur-X (EN 16931) in {outputFolder}");
        var pdfs = new List<string>();
        foreach (var json in jsonFiles)
        {
            var invoice = Invoice.Load(json);
            var computed = InvoiceTotals.Compute(invoice);
            var issuing = InvoiceTotals.ForIssuing(invoice);
            var result = FacturXWriter.Issue(invoice, outputFolder, writeXml);
            pdfs.Add(result.PdfPath);

            Console.WriteLine();
            Console.WriteLine($"  {invoice.Number}  {invoice.Seller.Name} -> {invoice.Buyer.Name}");
            Console.WriteLine($"    net {Money.Display(issuing.TaxBasisTotal)} + VAT {Money.Display(issuing.TaxTotal)} = {Money.Display(issuing.GrandTotal)} {invoice.Currency}" +
                              $"  (VAT rows: {string.Join(", ", issuing.VatBreakdown.Select(v => $"{v.Category} {Money.Rate(v.Rate)}%"))})");
            if (issuing.FromDeclaredValues && issuing.GrandTotal != computed.GrandTotal)
                Console.WriteLine($"    WARNING: declared totals used as is, computed gross would be {Money.Display(computed.GrandTotal)}");
            PrintResult(result);
        }
        return pdfs;
    }

    private static void Embed(List<string> inputs, string outputFolder)
    {
        string? pdf = inputs.FirstOrDefault(i => i.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        string? xml = inputs.FirstOrDefault(i => i.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        if (pdf == null || xml == null) throw new IOException("embed needs a PDF file and a CII XML file.");
        Console.WriteLine($"Embed: {pdf} + {xml} to Factur-X in {outputFolder}");
        PrintResult(FacturXWriter.Embed(pdf, xml, outputFolder));
    }

    private static void PrintResult(IssueResult result)
    {
        Console.WriteLine($"    {string.Join(" | ", result.Timings.Select(t => $"{t.Step} {t.Ms} ms"))}");
        Console.WriteLine($"    -> {result.PdfPath} ({result.Pages} page(s), {new FileInfo(result.PdfPath).Length / 1024} KB)" +
                          (result.XmlPath != null ? $" and {Path.GetFileName(result.XmlPath)}" : ""));
        Console.WriteLine($"    GdPicture PDF/A-3b validation: {(result.GdPicturePdfaValid ? "compliant" : "NOT compliant")}");
        if (!result.GdPicturePdfaValid) Console.WriteLine(result.GdPictureReport);
    }

    private static int Read(IReadOnlyList<string> pdfFiles, string reportPath)
    {
        if (pdfFiles.Count == 0) throw new IOException("No PDF file found.");
        string pageFolder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "pages");
        Console.WriteLine();
        Console.WriteLine($"Incoming: {pdfFiles.Count} PDF(s) checked");
        var reports = new List<InvoiceCheckReport>();
        foreach (var file in pdfFiles)
        {
            var received = FacturXReader.Read(file, pageFolder);
            var report = InvoiceChecks.Run(received);
            reports.Add(report);
            var inv = report.Invoice;

            Console.WriteLine();
            Console.WriteLine($"  {Path.GetFileName(file)}: {report.Verdict}");
            Console.WriteLine($"    {InvoiceChecks.Pretty(received.PdfaConformance)}, attachment {received.InvoiceFileName ?? "none"}, XMP {received.Xmp?.ConformanceLevel ?? "none"}");
            if (inv != null)
            {
                Console.WriteLine($"    {inv.Number} issued {inv.IssueDate:yyyy-MM-dd} by {inv.Seller.Name} ({inv.Seller.VatId}) to {inv.Buyer.Name}");
                Console.WriteLine($"    {inv.Lines.Count} lines, net {Fmt(inv.TaxBasisTotal)} + VAT {Fmt(inv.TaxTotal)} = gross {Fmt(inv.GrandTotal)}, due {Fmt(inv.DuePayable)} {inv.Currency}" +
                                  (inv.DueDate is { } due ? $" by {due:yyyy-MM-dd}" : ""));
            }
            foreach (var c in report.Checks.Where(c => c.Status != CheckStatus.Pass))
                Console.WriteLine($"    {c.Status.ToString().ToUpperInvariant(),-4} [{c.Rule}] {c.Title}: {c.Detail}");
            Console.WriteLine($"    {report.Checks.Count(c => c.Status == CheckStatus.Pass)}/{report.Checks.Count} checks passed");
        }
        ReportHtml.Write(reportPath, reports);
        Console.WriteLine();
        Console.WriteLine($"Report: {reportPath}");
        return reports.Any(r => r.Failed > 0) ? 3 : 0;
    }

    private static string Fmt(decimal? v) => v is { } d ? Money.Display(d) : "?";

    private static void RegisterLicense()
    {
        // RegisterKEY must always be called: an empty key starts evaluation mode (watermarked output).
        string key = (Environment.GetEnvironmentVariable("GDPICTURE_LICENSE_KEY") ?? "").Trim();
        bool ok;
        try { ok = new LicenseManager().RegisterKEY(key); }
        catch (Exception ex) when (key.Length > 0) { Console.WriteLine($"The license key was rejected ({ex.Message})."); ok = false; }
        if (!ok && key.Length > 0)
        {
            Console.WriteLine("Falling back to evaluation mode.");
            key = "";
            new LicenseManager().RegisterKEY("");
        }
        if (key.Length == 0) Console.WriteLine("No license key: GdPicture.NET runs in evaluation mode (watermarked output).");
    }

    /// <summary>The HTML converter drives Chrome. Use CHROME_PATH, else a browser GdPicture finds, else download chrome-headless-shell.</summary>
    private static void PrepareBrowser()
    {
        string chrome = (Environment.GetEnvironmentVariable("CHROME_PATH") ?? "").Trim();
        if (chrome.Length > 0)
        {
            if (!File.Exists(chrome)) throw new IOException($"CHROME_PATH is set to {chrome}, which does not exist.");
            GdPictureDocumentUtilities.SetWebBrowserPath(chrome);
        }
        else if (!GdPictureDocumentUtilities.IsWebBrowserAvailable())
        {
            string folder = Path.GetFullPath(".chrome");
            Console.WriteLine($"No Chrome found: downloading chrome-headless-shell to {folder} ...");
            GdPictureDocumentUtilities.SetWebBrowserPath(GdPictureDocumentUtilities.FetchLatestChromeHeadlessShell(folder));
        }
    }

    private static List<string> ExpandInputs(List<string> inputs, string pattern)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input)) files.AddRange(Directory.GetFiles(input, pattern).OrderBy(f => f, StringComparer.Ordinal));
            else if (File.Exists(input)) files.Add(input);
            else throw new IOException($"{input} does not exist.");
        }
        return files;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
Factur-X / ZUGFeRD hybrid e-invoices with GdPicture.NET

Usage:
  dotnet run --project EInvoicing -- demo                     Issue the sample invoices, then check them and samples/incoming
  dotnet run --project EInvoicing -- create [json|folder]...  Invoice JSON to Factur-X PDF/A-3b (default: samples/invoices)
  dotnet run --project EInvoicing -- embed invoice.pdf invoice.xml   Existing PDF + CII XML to Factur-X PDF/A-3b
  dotnet run --project EInvoicing -- read [pdf|folder]...     Check received Factur-X / ZUGFeRD PDFs (default: output)
  dotnet run --project EInvoicing -- make-incoming-samples    Regenerate samples/incoming from samples/incoming-data

Options:
  -o, --output DIR    Output folder (default: output)
  -r, --report FILE   HTML report path (default: <output>/report.html)

Exit code of read: 0 when every invoice passes, 3 when at least one check fails.
""");
    }

    private sealed class Options
    {
        public List<string> Inputs { get; } = new();
        public string Output { get; private set; } = "output";
        public string? Report { get; private set; }

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-o" or "--output" when i + 1 < args.Length: o.Output = args[++i]; break;
                    case "-r" or "--report" when i + 1 < args.Length: o.Report = args[++i]; break;
                    default: o.Inputs.Add(args[i]); break;
                }
            }
            return o;
        }
    }
}
