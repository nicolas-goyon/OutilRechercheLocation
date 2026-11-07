using RechercheLogement.Core.Recommend;
using SkiaSharp;

namespace RechercheLogement.Server.Recommend;

/// <summary>
/// Descripteurs d'une photo (<see cref="PhotoStats"/>), calculés sur ses pixels. L'image est réduite à 96 pixels
/// de large : assez pour les couleurs et les contours, rapide.
/// </summary>
public static class PhotoAnalyzer
{
    private const int Width = 96;

    /// <summary>Image illisible (format inconnu, fichier tronqué) : null.</summary>
    public static PhotoStats? Analyze(byte[] image)
    {
        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data); // format inconnu : null (Decode lèverait une exception)
        if (codec is null) return null;
        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null || decoded.Width < 8 || decoded.Height < 8) return null;
        var height = Math.Max(8, decoded.Height * Width / decoded.Width);
        using var small = decoded.Resize(new SKImageInfo(Width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear));
        return small is null ? null : Analyze(small);
    }

    /// <summary>
    /// Vérifie que la bibliothèque native de SkiaSharp se charge (image Docker) : encode puis analyse une image verte.
    /// Erreur : son message, sinon null.
    /// </summary>
    public static string? SelfTest()
    {
        try
        {
            using var bmp = new SKBitmap(32, 32);
            bmp.Erase(new SKColor(60, 140, 50));
            using var image = SKImage.FromBitmap(bmp);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            var stats = Analyze(png.ToArray());
            return stats is { Green: > 0.9 } ? null : "image de test mal lue";
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            return e.Message;
        }
    }

    public static PhotoStats Analyze(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, n = w * h;
        var value = new double[n];
        double green = 0, sky = 0, snow = 0, warm = 0, dark = 0, bright = 0, sat = 0;
        var top = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                c.ToHsv(out var hue, out var s, out var v);
                s /= 100f;
                v /= 100f;
                value[y * w + x] = v;
                bright += v;
                sat += s;
                if (v < 0.2) dark++;
                if (v > 0.85 && s < 0.15) snow++;
                if (s > 0.2 && v > 0.15 && hue is >= 65 and <= 170) green++;
                if (s > 0.25 && v > 0.2 && (hue < 45 || hue > 340)) warm++;
                if (y < h / 3)
                {
                    top++;
                    if (hue is >= 185 and <= 250 && s > 0.12 && v > 0.45) sky++;
                }
            }
        }
        // Contours : écart de luminosité avec les voisins de droite et du dessous, au-delà d'un seuil.
        double edges = 0;
        for (var y = 0; y < h - 1; y++)
            for (var x = 0; x < w - 1; x++)
            {
                var i = y * w + x;
                if (Math.Abs(value[i] - value[i + 1]) + Math.Abs(value[i] - value[i + w]) > 0.15) edges++;
            }
        return new PhotoStats(green / n, top == 0 ? 0 : sky / top, snow / n, warm / n, dark / n, bright / n, sat / n,
            edges / ((w - 1.0) * (h - 1.0)));
    }
}
