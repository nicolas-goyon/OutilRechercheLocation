namespace RechercheLogement.Core.Recommend;

/// <summary>
/// Descripteurs simples d'une photo, calculés sur le serveur à partir des pixels (aucun service extérieur) :
/// proportions de pixels d'une teinte donnée et mesures globales, toutes entre 0 et 1.
/// </summary>
/// <param name="Green">Végétation : pixels verts assez saturés (prairie, forêt, jardin).</param>
/// <param name="Sky">Ciel : pixels bleus clairs dans le tiers haut de l'image.</param>
/// <param name="Snow">Blanc : pixels très clairs et peu saturés (neige, murs blancs).</param>
/// <param name="Warm">Tons chauds : rouges, oranges, bruns saturés (bois, pierre, tuiles).</param>
/// <param name="Dark">Pixels sombres (intérieurs peu éclairés, photos de nuit).</param>
/// <param name="Bright">Luminosité moyenne.</param>
/// <param name="Saturation">Saturation moyenne (couleurs vives ou ternes).</param>
/// <param name="Detail">Densité de contours (scène chargée ou épurée).</param>
public sealed record PhotoStats(double Green, double Sky, double Snow, double Warm, double Dark, double Bright, double Saturation, double Detail)
{
    /// <summary>Noms des descripteurs, dans l'ordre de <see cref="Values"/>.</summary>
    public static readonly IReadOnlyList<string> Names = ["green", "sky", "snow", "warm", "dark", "bright", "saturation", "detail"];

    public IReadOnlyList<double> Values => [Green, Sky, Snow, Warm, Dark, Bright, Saturation, Detail];

    /// <summary>Moyenne de plusieurs photos d'un même bien. Aucune : null.</summary>
    public static PhotoStats? Average(IReadOnlyCollection<PhotoStats> photos)
    {
        if (photos.Count == 0) return null;
        double Avg(Func<PhotoStats, double> f) => photos.Average(f);
        return new PhotoStats(Avg(p => p.Green), Avg(p => p.Sky), Avg(p => p.Snow), Avg(p => p.Warm),
            Avg(p => p.Dark), Avg(p => p.Bright), Avg(p => p.Saturation), Avg(p => p.Detail));
    }
}
