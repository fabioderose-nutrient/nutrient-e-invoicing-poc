using System.Xml.Linq;
using GdPicture14;

namespace EInvoicing;

/// <summary>What GdPicture finds in a received PDF: PDF/A level, attachments, Factur-X XMP metadata, the invoice XML and the visible text.</summary>
public sealed class ReceivedPdf
{
    public string Path { get; init; } = "";
    public int Pages { get; set; }
    public string PdfaConformance { get; set; } = "";
    public List<string> EmbeddedFiles { get; } = new();
    public string? InvoiceFileName { get; set; }
    public byte[]? InvoiceXml { get; set; }
    public XmpInvoiceInfo? Xmp { get; set; }
    public string VisibleText { get; set; } = "";
    public string? PageImagePath { get; set; }
}

/// <summary>The Factur-X / ZUGFeRD PDF/A extension schema fields of the XMP metadata.</summary>
public sealed record XmpInvoiceInfo(string Namespace, string? DocumentType, string? DocumentFileName, string? Version, string? ConformanceLevel);

public static class FacturXReader
{
    /// <summary>Names used for the invoice XML by Factur-X 1.x and ZUGFeRD 1.x / 2.x.</summary>
    public static readonly string[] KnownInvoiceFileNames =
        { "factur-x.xml", "zugferd-invoice.xml", "ZUGFeRD-invoice.xml", "xrechnung.xml" };

    public static ReceivedPdf Read(string pdfPath, string? pageImageFolder)
    {
        var result = new ReceivedPdf { Path = pdfPath };
        using var pdf = new GdPicturePDF();
        FacturXWriter.Check(pdf.LoadFromFile(pdfPath, false), $"open {System.IO.Path.GetFileName(pdfPath)}");

        result.Pages = pdf.GetPageCount();
        result.PdfaConformance = pdf.GetPDFAConformance().ToString();
        result.Xmp = ParseXmp(pdf.GetMetadata());

        // Attachments: pick the file the XMP names, else one of the standard names.
        int count = pdf.GetEmbeddedFileCount();
        for (int i = 0; i < count; i++) result.EmbeddedFiles.Add(pdf.GetEmbeddedFileName(i));
        int index = result.EmbeddedFiles.FindIndex(n => result.Xmp?.DocumentFileName != null &&
                                                        string.Equals(n, result.Xmp.DocumentFileName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = result.EmbeddedFiles.FindIndex(n => KnownInvoiceFileNames.Contains(n, StringComparer.OrdinalIgnoreCase));
        if (index >= 0)
        {
            byte[] data = Array.Empty<byte>();
            if (pdf.ExtractEmbeddedFile(index, ref data) == GdPictureStatus.OK)
            {
                result.InvoiceFileName = result.EmbeddedFiles[index];
                result.InvoiceXml = data;
            }
        }

        // Visible text of every page, to compare the human-readable invoice with the XML.
        var text = new System.Text.StringBuilder();
        for (int page = 1; page <= result.Pages; page++)
        {
            pdf.SelectPage(page);
            text.AppendLine(pdf.GetPageText());
        }
        result.VisibleText = text.ToString();

        if (pageImageFolder != null)
        {
            Directory.CreateDirectory(pageImageFolder);
            pdf.SelectPage(1);
            int image = pdf.RenderPageToGdPictureImageEx(110, true);
            if (image != 0)
            {
                string png = System.IO.Path.Combine(pageImageFolder, System.IO.Path.GetFileNameWithoutExtension(pdfPath) + ".page1.png");
                using var imaging = new GdPictureImaging();
                if (imaging.SaveAsPNG(image, png) == GdPictureStatus.OK) result.PageImagePath = png;
                imaging.ReleaseGdPictureImage(image);
            }
        }
        pdf.CloseDocument();
        return result;
    }

    /// <summary>Reads the fx: (Factur-X) or zf: (ZUGFeRD) properties, whether written as elements or as attributes.</summary>
    public static XmpInvoiceInfo? ParseXmp(string? xmp)
    {
        if (string.IsNullOrWhiteSpace(xmp)) return null;
        XDocument doc;
        try { doc = XDocument.Parse(xmp); }
        catch (System.Xml.XmlException) { return null; }

        static bool IsInvoiceNs(string ns) =>
            ns.StartsWith("urn:factur-x:pdfa:CrossIndustryDocument", StringComparison.OrdinalIgnoreCase) ||
            ns.StartsWith("urn:zugferd:pdfa:CrossIndustryDocument", StringComparison.OrdinalIgnoreCase) ||
            ns.StartsWith("urn:ferd:pdfa:CrossIndustryDocument", StringComparison.OrdinalIgnoreCase);

        var values = new Dictionary<string, string>();
        string? ns = null;
        foreach (var description in doc.Descendants(XName.Get("Description", "http://www.w3.org/1999/02/22-rdf-syntax-ns#")))
        {
            foreach (var e in description.Elements().Where(e => IsInvoiceNs(e.Name.NamespaceName)))
            {
                values[e.Name.LocalName] = e.Value.Trim();
                ns = e.Name.NamespaceName;
            }
            foreach (var a in description.Attributes().Where(a => IsInvoiceNs(a.Name.NamespaceName)))
            {
                values[a.Name.LocalName] = a.Value.Trim();
                ns = a.Name.NamespaceName;
            }
        }
        if (ns == null) return null;
        return new XmpInvoiceInfo(ns, values.GetValueOrDefault("DocumentType"), values.GetValueOrDefault("DocumentFileName"),
                                  values.GetValueOrDefault("Version"), values.GetValueOrDefault("ConformanceLevel"));
    }

    /// <summary>The XMP ConformanceLevel that matches a CII guideline ID (BT-24).</summary>
    public static string? ExpectedConformanceLevel(string? profileId)
    {
        if (profileId == null) return null;
        string p = profileId.ToLowerInvariant();
        if (p.EndsWith(":minimum")) return "MINIMUM";
        if (p.EndsWith(":basicwl")) return "BASIC WL";
        if (p.EndsWith(":basic")) return "BASIC";
        if (p.EndsWith(":extended")) return "EXTENDED";
        if (p.Contains("xrechnung")) return "XRECHNUNG";
        if (p == "urn:cen.eu:en16931:2017" || p.EndsWith(":comfort")) return "EN 16931";
        return null;
    }
}
