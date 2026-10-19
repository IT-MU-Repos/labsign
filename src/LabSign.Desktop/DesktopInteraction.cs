using Avalonia.Threading;
using LabSign.Core;
using LabSign.Core.Signing;
using SkiaSharp;

namespace LabSign.Desktop;

/// <summary>The PIN window and the stamp image: the two parts that need a desktop.</summary>
internal sealed class DesktopInteraction(string stampImagePath) : ISignerInteraction
{
    public async Task<string?> RequestPinAsync(PinRequest request, CancellationToken cancellationToken)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = new PinWindow(request);
            var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(window.Close));
            window.Closed += (_, _) => registration.Dispose();
            window.Show();
            return window.Result;
        });
    }

    /// <summary>
    /// LIMS_SOURCE CreateSignatureWithText, on SkiaSharp so it draws the same on Windows and
    /// Linux: the handwritten signature stretched to 3000×975 (300 dpi), with "Digitally signed
    /// by / name / Date / when" right-aligned and centred over it in 28 pt Arial. Without
    /// signature.png the text is drawn alone.
    /// </summary>
    public byte[]? RenderStamp(string signerName, DateTime signedAt)
    {
        const int width = 3000, height = 975;
        const float fontPixels = 28f * 300f / 72f; // 28 pt at 300 dpi
        try
        {
            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                if (File.Exists(stampImagePath))
                {
                    using var signature = SKBitmap.Decode(stampImagePath);
                    if (signature is not null)
                        canvas.DrawBitmap(signature, new SKRect(0, 0, width, height), new SKPaint { FilterQuality = SKFilterQuality.High });
                }

                string[] lines = ["Digitally signed by:", signerName, "Date:", $"{signedAt:yyyy-MM-dd HH:mm}"];
                using var typeface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
                using var paint = new SKPaint
                {
                    Color = SKColors.Black,
                    IsAntialias = true,
                    TextSize = fontPixels,
                    Typeface = typeface,
                    TextAlign = SKTextAlign.Right,
                };
                var lineHeight = paint.FontSpacing;
                var top = (height - lineHeight * lines.Length) / 2 - paint.FontMetrics.Ascent;
                for (var i = 0; i < lines.Length; i++)
                    canvas.DrawText(lines[i], width, top + i * lineHeight, paint);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
        catch (Exception ex)
        {
            Log.Write($"Stamp image could not be drawn, using text only: {ex.Message}");
            return null;
        }
    }
}
