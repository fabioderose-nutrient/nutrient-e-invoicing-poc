using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using GdPicture14;

namespace EInvoicing;

public sealed record IssueResult(string PdfPath, string? XmlPath, int Pages, bool GdPicturePdfaValid, string GdPictureReport, IReadOnlyList<(string Step, long Ms)> Timings);

/// <summary>
/// Outgoing side: a Factur-X / ZUGFeRD hybrid invoice with GdPicture.NET.
/// HTML to PDF, PDF to PDF/A-3b, then the CII XML is embedded as factur-x.xml with the Factur-X XMP metadata.
/// </summary>
public static class FacturXWriter
{
    /// <summary>Invoice data (JSON) to a Factur-X PDF: renders the invoice, then converts and embeds.</summary>
    public static IssueResult Issue(Invoice invoice, string outputFolder, bool writeXml = true)
    {
        Directory.CreateDirectory(outputFolder);
        var timings = new List<(string, long)>();
        var sw = Stopwatch.StartNew();
        string baseName = FileNames.Safe(invoice.Number);
        string pdfPath = Path.Combine(outputFolder, baseName + ".pdf");
        string? xmlPath = writeXml ? Path.Combine(outputFolder, baseName + ".factur-x.xml") : null;

        using var work = new WorkFolder();
        var totals = InvoiceTotals.ForIssuing(invoice);

        // 1. Structured data: the CII XML, EN 16931 profile.
        byte[] xml = CiiWriter.Write(invoice, totals);
        if (xmlPath != null) File.WriteAllBytes(xmlPath, xml);
        Step(timings, "CII XML", sw);

        // 2. Human-readable invoice: HTML rendered to PDF by the GdPicture HTML converter (Chrome engine).
        string plainPdf = work.File("plain.pdf");
        using (var converter = new GdPictureDocumentConverter())
        {
            converter.HtmlPageWidth = 595.28f;   // A4 in points
            converter.HtmlPageHeight = 841.89f;
            converter.HtmlPreferCSSPageSize = true;
            converter.HtmlPreferOnePage = false;
            converter.HtmlEmulationType = EmulationMedia.Print;
            using var html = new MemoryStream(Encoding.UTF8.GetBytes(InvoiceHtml.Render(invoice, totals)));
            Check(converter.LoadFromStream(html, GdPicture14.DocumentFormat.DocumentFormatHTML), "load the invoice HTML");
            Check(converter.SaveAsPDF(plainPdf, PdfConformance.PDF), "convert the invoice HTML to PDF");
        }
        Step(timings, "HTML to PDF", sw);

        var metadata = new PdfMetadata($"Invoice {invoice.Number}", invoice.Seller.Name,
                                       $"Invoice {invoice.Number} from {invoice.Seller.Name} to {invoice.Buyer.Name}");
        return ToFacturX(plainPdf, xml, pdfPath, xmlPath, metadata, work, timings, sw);
    }

    /// <summary>An existing PDF invoice (for example from an ERP) plus its CII XML to a Factur-X PDF.</summary>
    public static IssueResult Embed(string sourcePdf, string sourceXml, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var timings = new List<(string, long)>();
        var sw = Stopwatch.StartNew();
        byte[] xml = File.ReadAllBytes(sourceXml);
        var doc = XDocument.Parse(Encoding.UTF8.GetString(xml).TrimStart('﻿'));
        if (!CiiReader.IsCii(doc))
            throw new InvalidDataException($"{sourceXml} is not a CII CrossIndustryInvoice (Factur-X / ZUGFeRD 2.x syntax).");
        var parsed = CiiReader.Parse(doc);
        string number = parsed.Number ?? Path.GetFileNameWithoutExtension(sourcePdf);
        string pdfPath = Path.Combine(outputFolder, FileNames.Safe(number) + ".pdf");
        if (Path.GetFullPath(pdfPath) == Path.GetFullPath(sourcePdf))
            pdfPath = Path.Combine(outputFolder, FileNames.Safe(number) + ".factur-x.pdf");
        Step(timings, "Read CII XML", sw);

        using var work = new WorkFolder();
        var metadata = new PdfMetadata($"Invoice {number}", parsed.Seller.Name ?? "",
                                       $"Invoice {number} from {parsed.Seller.Name} to {parsed.Buyer.Name}");
        return ToFacturX(sourcePdf, xml, pdfPath, null, metadata, work, timings, sw);
    }

    private sealed record PdfMetadata(string Title, string Author, string Subject);

    private static IssueResult ToFacturX(string sourcePdf, byte[] xml, string pdfPath, string? xmlPath, PdfMetadata metadata,
                                         WorkFolder work, List<(string, long)> timings, Stopwatch sw)
    {
        // 3. PDF/A-3b: the archival format that allows embedded files (ISO 19005-3).
        string pdfa = work.File("pdfa3.pdf");
        using (var pdf = new GdPicturePDF())
        {
            Check(pdf.LoadFromFile(sourcePdf, true), "load the source PDF");
            Check(pdf.ConvertToPDFA(pdfa, PdfConversionConformance.PDF_A_3b, true, true), "convert to PDF/A-3b");
            pdf.CloseDocument();
        }
        Step(timings, "PDF/A-3b conversion", sw);

        // 4. Factur-X: embed the XML as factur-x.xml. GdPicture writes the file specification with
        //    AFRelationship /Data, the /AF array of the catalog, the text/xml subtype and the Factur-X
        //    PDF/A extension schema in the XMP metadata (fx:DocumentType, DocumentFileName, Version, ConformanceLevel).
        int pages;
        using (var pdf = new GdPicturePDF())
        {
            Check(pdf.LoadFromFile(pdfa, true), "load the PDF/A-3b file");
            pdf.SetTitle(metadata.Title);
            pdf.SetAuthor(metadata.Author);
            pdf.SetSubject(metadata.Subject);
            using var xmlStream = new MemoryStream(xml);
            Check(pdf.EmbedInvoiceData(xmlStream, PdfInvoiceDataFormat.FacturX_1_0), "embed the Factur-X XML");
            Check(pdf.SaveToFile(pdfPath), "save the Factur-X PDF");
            pages = pdf.GetPageCount();
            pdf.CloseDocument();
        }
        Step(timings, "Embed factur-x.xml", sw);

        // 5. Self-check of the saved file with the GdPicture PDF/A validator.
        bool valid;
        string report = "";
        using (var pdf = new GdPicturePDF())
        {
            Check(pdf.LoadFromFile(pdfPath, false), "reload the Factur-X PDF");
            valid = pdf.CheckPDFAConformance(PdfValidationConformance.PDF_A_3b, ref report);
            pdf.CloseDocument();
        }
        Step(timings, "PDF/A-3b validation", sw);

        return new IssueResult(pdfPath, xmlPath, pages, valid, report, timings);
    }

    private static void Step(List<(string, long)> timings, string name, Stopwatch sw)
    {
        timings.Add((name, sw.ElapsedMilliseconds));
        sw.Restart();
    }

    internal static void Check(GdPictureStatus status, string what)
    {
        if (status != GdPictureStatus.OK)
            throw new InvalidOperationException($"Could not {what}: GdPicture status {status}.");
    }

    /// <summary>Temporary folder for the intermediate PDFs, deleted afterwards.</summary>
    private sealed class WorkFolder : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "einvoicing-" + Guid.NewGuid().ToString("N"));
        public WorkFolder() => Directory.CreateDirectory(_path);
        public string File(string name) => Path.Combine(_path, name);
        public void Dispose()
        {
            try { Directory.Delete(_path, true); } catch (IOException) { }
        }
    }
}

public static class FileNames
{
    public static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());
    }
}
