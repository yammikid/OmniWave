using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Services;

public static class ColorUtils
{
    public static BitmapImage? BytesToBitmap(byte[]? bytes, int decodePixelWidth = 0)
    {
        if (bytes == null || bytes.Length == 0) return null;

        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (decodePixelWidth > 0)
                bmp.DecodePixelWidth = decodePixelWidth;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static Color ExtractAmbientColor(BitmapSource? source, Color fallback)
    {
        if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            return fallback;

        try
        {
            double scaleX = 28.0 / source.PixelWidth;
            double scaleY = 28.0 / source.PixelHeight;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth;
            int h = bgra.PixelHeight;
            if (w <= 0 || h <= 0) return fallback;

            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            double rSum = 0, gSum = 0, bSum = 0, weightSum = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];
                if (a < 24) continue;

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                double sat = max == 0 ? 0 : (max - min) / (double)max;
                double lum = (r + g + b) / (3.0 * 255.0);

                // Приоритет отдаётся ярким и насыщенным пикселям обложки
                double lumWeight = lum > 0.12 && lum < 0.90 ? 1.0 : 0.22;
                double weight = (0.14 + sat * 2.8) * lumWeight * (a / 255.0);

                rSum += r * weight;
                gSum += g * weight;
                bSum += b * weight;
                weightSum += weight;
            }

            if (weightSum <= 0.0001) return fallback;

            byte fr = (byte)Math.Clamp(rSum / weightSum, 28, 242);
            byte fg = (byte)Math.Clamp(gSum / weightSum, 28, 242);
            byte fb = (byte)Math.Clamp(bSum / weightSum, 28, 242);
            return Color.FromRgb(fr, fg, fb);
        }
        catch
        {
            return fallback;
        }
    }

    public static Color ParseHex(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var parsed = ColorConverter.ConvertFromString(hex);
            if (parsed is Color c) return c;
        }
        catch { }
        return fallback;
    }
}
