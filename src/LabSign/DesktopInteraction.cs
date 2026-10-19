using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using LabSign.Core;
using LabSign.Core.Signing;

namespace LabSign;

/// <summary>The PIN dialog and the stamp image, both of which need Windows.</summary>
internal sealed class DesktopInteraction(SynchronizationContext ui, string stampImagePath) : ISignerInteraction
{
    public Task<string?> RequestPinAsync(PinRequest request, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(_ =>
        {
            try
            {
                using var dialog = new PinDialog(request);
                using var registration = cancellationToken.Register(() => dialog.BeginInvoke(dialog.Close));
                result.TrySetResult(dialog.ShowDialog() == DialogResult.OK ? dialog.Pin : null);
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        }, null);
        return result.Task;
    }

    /// <summary>
    /// LIMS_SOURCE CreateSignatureWithText: the handwritten signature stretched to 3000×975 at
    /// 300 dpi with the signer and date right-aligned over it. Without signature.png the text is
    /// drawn alone, where legacy would have failed the batch.
    /// </summary>
    public byte[]? RenderStamp(string signerName, DateTime signedAt)
    {
        const int width = 3000, height = 975;
        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            bitmap.SetResolution(300, 300);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);

                if (File.Exists(stampImagePath))
                {
                    using var signature = Image.FromFile(stampImagePath);
                    g.DrawImage(signature, 0, 0, width, height);
                }

                var text = $"Digitally signed by:\n{signerName}\nDate:\n{signedAt:yyyy-MM-dd HH:mm}";
                using var font = new Font("Arial", 28, FontStyle.Regular);
                using var brush = new SolidBrush(Color.Black);
                using var format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, brush, new RectangleF(0, 0, width, height), format);
            }
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return png.ToArray();
        }
        catch (Exception ex)
        {
            Log.Write($"Stamp image could not be drawn, using text only: {ex.Message}");
            return null;
        }
    }
}
