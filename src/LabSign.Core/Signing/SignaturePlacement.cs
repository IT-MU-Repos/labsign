using iText.Kernel.Geom;
using UglyToad.PdfPig;

namespace LabSign.Core.Signing;

/// <summary>Where on which page the signature box goes (PDF points, 1-based page).</summary>
public sealed record SignatureSpot(int PageNumber, Rectangle Rect, bool FromMarker);

/// <summary>
/// The SignHere! marker decides when the document has one (as every LIMS report does). A
/// report must have it; any other document (Sign a document) may instead name a page
/// (<c>first</c>, <c>last</c> or a number) and a corner (<c>bottom-right</c>, <c>bottom-left</c>,
/// <c>top-right</c>, <c>top-left</c>), and the 200×65 box sits there 36 pt from the edges.
/// </summary>
public static class SignaturePlacement
{
    public const float Width = 200, Height = 65, Margin = 36;
    public static readonly string[] Corners = ["bottom-right", "bottom-left", "top-right", "top-left"];

    /// <summary>The spot, or an error a person can act on.</summary>
    public static (SignatureSpot? Spot, string? Error) Resolve(byte[] pdf, bool requireMarker, string? page, string? corner, string filename)
    {
        var marker = SignHereFinder.Find(pdf);
        if (marker is not null)
            return (new SignatureSpot(marker.PageNumber, PdfReportSigner.Placement(marker), FromMarker: true), null);
        if (requireMarker)
            return (null, $"\"{SignHereFinder.Marker}\" not found in {filename}.");

        corner = string.IsNullOrWhiteSpace(corner) ? "bottom-right" : corner.Trim().ToLowerInvariant();
        if (!Corners.Contains(corner))
            return (null, $"corner must be one of {string.Join(", ", Corners)}.");

        using var document = PdfDocument.Open(pdf);
        var count = document.NumberOfPages;
        var spec = string.IsNullOrWhiteSpace(page) ? "last" : page.Trim().ToLowerInvariant();
        int number;
        if (spec == "last") number = count;
        else if (spec == "first") number = 1;
        else if (!int.TryParse(spec, out number) || number < 1 || number > count)
            return (null, $"{filename} has {count} page(s); page {spec} does not exist.");

        var target = document.GetPage(number);
        float pageWidth = (float)target.Width, pageHeight = (float)target.Height;
        var x = corner.EndsWith("left") ? Margin : pageWidth - Margin - Width;
        var y = corner.StartsWith("bottom") ? Margin : pageHeight - Margin - Height;
        return (new SignatureSpot(number, new Rectangle(x, y, Width, Height), FromMarker: false), null);
    }
}
