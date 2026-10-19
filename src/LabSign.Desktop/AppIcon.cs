using SkiaSharp;

namespace LabSign.Desktop;

/// <summary>
/// The LabSign icon, drawn rather than shipped: a blue shield with a white tick. Used for the
/// tray, the PIN window and, on Linux, the desktop entries (written to the data folder).
/// </summary>
internal static class AppIcon
{
    public static byte[] Png(int size)
    {
        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var s = size / 64f;
            using var shield = new SKPath();
            shield.MoveTo(32 * s, 4 * s);
            shield.LineTo(56 * s, 13 * s);
            shield.CubicTo(56 * s, 36 * s, 47 * s, 52 * s, 32 * s, 60 * s);
            shield.CubicTo(17 * s, 52 * s, 8 * s, 36 * s, 8 * s, 13 * s);
            shield.Close();
            using var fill = new SKPaint { Color = new SKColor(0x1F, 0x5F, 0xBF), IsAntialias = true };
            canvas.DrawPath(shield, fill);

            using var tick = new SKPath();
            tick.MoveTo(21 * s, 33 * s);
            tick.LineTo(29 * s, 41 * s);
            tick.LineTo(44 * s, 24 * s);
            using var stroke = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 6 * s,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };
            canvas.DrawPath(tick, stroke);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
