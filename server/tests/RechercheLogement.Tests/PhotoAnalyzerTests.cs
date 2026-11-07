using RechercheLogement.Server.Recommend;
using SkiaSharp;

namespace RechercheLogement.Tests;

/// <summary>Descripteurs de photos, sur des images dessinées : prairie, neige, ciel au-dessus d'un pré.</summary>
public class PhotoAnalyzerTests
{
    private static byte[] Jpeg(int width, int height, Action<SKCanvas> draw)
    {
        using var bmp = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bmp)) draw(canvas);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static void Fill(SKCanvas c, SKColor color, SKRect? rect = null)
    {
        using var paint = new SKPaint { Color = color };
        if (rect is { } r) c.DrawRect(r, paint);
        else c.Clear(color);
    }

    [Fact]
    public void Meadow_IsGreen()
    {
        var s = PhotoAnalyzer.Analyze(Jpeg(320, 240, c => Fill(c, new SKColor(60, 140, 50))))!;
        Assert.True(s.Green > 0.9, $"{s.Green}");
        Assert.True(s.Snow < 0.05 && s.Sky < 0.05);
        Assert.True(s.Detail < 0.05, $"{s.Detail}"); // uni : aucun contour
    }

    [Fact]
    public void Snow_IsWhiteAndBright()
    {
        var s = PhotoAnalyzer.Analyze(Jpeg(320, 240, c => Fill(c, new SKColor(245, 247, 250))))!;
        Assert.True(s.Snow > 0.9, $"{s.Snow}");
        Assert.True(s.Bright > 0.9, $"{s.Bright}");
        Assert.True(s.Green < 0.05);
    }

    [Fact]
    public void SkyOverMeadow()
    {
        var s = PhotoAnalyzer.Analyze(Jpeg(320, 240, c =>
        {
            Fill(c, new SKColor(60, 140, 50));
            Fill(c, new SKColor(110, 170, 235), new SKRect(0, 0, 320, 100));
        }))!;
        Assert.True(s.Sky > 0.9, $"{s.Sky}");          // tiers haut : ciel
        Assert.InRange(s.Green, 0.45, 0.7);            // le reste : pré
        Assert.True(s.Detail is > 0 and < 0.1, $"{s.Detail}"); // une ligne d'horizon
    }

    [Fact]
    public void Unreadable_IsNull() => Assert.Null(PhotoAnalyzer.Analyze([1, 2, 3, 4]));

    [Fact]
    public void SelfTest_Passes() => Assert.Null(PhotoAnalyzer.SelfTest());
}
