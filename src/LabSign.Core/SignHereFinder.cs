using UglyToad.PdfPig;

namespace LabSign.Core;

/// <summary>Where the report template wants the signature (LIMS_SOURCE TextFinder.FindSignHere).</summary>
public sealed record SignHereMarker(int PageNumber, double Left, double Top);

public static class SignHereFinder
{
    public const string Marker = "SignHere!";

    /// <summary>The first word that is exactly <c>SignHere!</c>, with a 1-based page number; null if absent.</summary>
    public static SignHereMarker? Find(string pdfPath)
    {
        using var document = PdfDocument.Open(pdfPath);
        return Find(document);
    }

    public static SignHereMarker? Find(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return Find(document);
    }

    private static SignHereMarker? Find(PdfDocument document)
    {
        foreach (var page in document.GetPages())
        {
            foreach (var word in page.GetWords())
            {
                if (word.Text == Marker)
                    return new SignHereMarker(page.Number, word.BoundingBox.Left, word.BoundingBox.Top);
            }
        }
        return null;
    }
}
