using iText.Commons.Bouncycastle.Cert;
using iText.Forms.Form.Element;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Signatures;

namespace LabSign.Core.Signing;

public sealed record StampContent(byte[]? PngImage, string Text);

/// <summary>One PDF signature, placed where SignReports.vb puts it.</summary>
public static class PdfReportSigner
{
    public const string FieldName = "Signature1";

    /// <summary>
    /// Signs <paramref name="inputPath"/> into <paramref name="outputPath"/>. The output is written
    /// to a hidden partial file in the same folder and renamed into place, so a crash never leaves
    /// a half-written report where the sender would pick it up.
    /// </summary>
    public static void Sign(
        string inputPath,
        string outputPath,
        SignHereMarker marker,
        StampContent stamp,
        string reason,
        string location,
        IExternalSignature signature,
        IX509Certificate[] chain,
        ITSAClient? tsa)
    {
        var folder = System.IO.Path.GetDirectoryName(outputPath)!;
        var partial = System.IO.Path.Combine(folder, $".{System.IO.Path.GetFileName(outputPath)}.labsign-partial");
        try
        {
            using (var reader = new PdfReader(inputPath))
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var signer = new PdfSigner(reader, output, new StampingProperties());

                var appearance = new SignatureFieldAppearance(FieldName);
                if (stamp.PngImage is { Length: > 0 })
                    appearance.SetContent(ImageDataFactory.Create(stamp.PngImage));
                else
                    appearance.SetContent(stamp.Text);

                signer.SetSignerProperties(new SignerProperties()
                    .SetFieldName(FieldName)
                    .SetReason(reason)
                    .SetLocation(location)
                    .SetPageNumber(marker.PageNumber)
                    .SetPageRect(Placement(marker))
                    .SetSignatureAppearance(appearance));

                signer.SignDetached(signature, chain, null, null, tsa, 0, PdfSigner.CryptoStandard.CMS);
            }
            File.Move(partial, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    /// <summary>
    /// SignReports.vb: point = (round(left), round(top) − 58); Signing.vb: rect = (X, Y + 50, 200, 65).
    /// Math.Round is banker's rounding in both VB and C#, so the box lands on the same point.
    /// </summary>
    public static Rectangle Placement(SignHereMarker marker) =>
        new((float)Math.Round(marker.Left), (float)(Math.Round(marker.Top) - 58 + 50), 200, 65);
}
