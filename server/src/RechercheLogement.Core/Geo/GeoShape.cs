using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Model;

namespace RechercheLogement.Core.Geo;

/// <summary>
/// Zone de recherche : un cercle (centre + rayon) ou un ou plusieurs polygones (isochrone de temps de trajet).
/// Coordonnées en degrés (WGS84). Les calculs utilisent une projection locale : précision du mètre
/// largement suffisante à l'échelle d'une ville ou d'un département.
/// </summary>
public sealed class GeoShape
{
    /// <summary>Cercle : centre.</summary>
    public GeoPoint? Center { get; init; }
    /// <summary>Cercle : rayon en mètres.</summary>
    public double? RadiusM { get; init; }
    /// <summary>Polygones : contours extérieurs (le premier point n'a pas besoin d'être répété à la fin).</summary>
    public List<List<GeoPoint>> Rings { get; init; } = [];

    public static GeoShape Circle(GeoPoint center, double radiusM) => new() { Center = center, RadiusM = radiusM };

    public static GeoShape Polygons(IEnumerable<List<GeoPoint>> rings) => new() { Rings = rings.Where(r => r.Count >= 3).ToList() };

    public bool IsCircle => Center is not null && RadiusM is not null;

    /// <summary>Point dans la zone, ou à moins de <paramref name="toleranceM"/> mètres de son bord.</summary>
    public bool Contains(GeoPoint p, double toleranceM = 0)
    {
        if (IsCircle) return DedupScorer.DistanceMeters(Center!, p) <= RadiusM!.Value + toleranceM;
        foreach (var ring in Rings)
            if (InRing(ring, p) || (toleranceM > 0 && DistanceToRing(ring, p) <= toleranceM)) return true;
        return false;
    }

    /// <summary>Points du bord (cercle : <paramref name="count"/> points ; polygones : sommets échantillonnés).</summary>
    public IEnumerable<GeoPoint> BoundaryPoints(int count = 24)
    {
        if (IsCircle)
        {
            var c = Center!;
            var r = RadiusM!.Value;
            for (var i = 0; i < count; i++)
            {
                var a = 2 * Math.PI * i / count;
                yield return Offset(c, r * Math.Cos(a), r * Math.Sin(a));
            }
            yield break;
        }
        foreach (var ring in Rings)
        {
            var step = Math.Max(1, ring.Count / count);
            for (var i = 0; i < ring.Count; i += step) yield return ring[i];
        }
    }

    /// <summary>Rayon d'un cercle qui contient toute la zone autour de <paramref name="from"/> (mètres).</summary>
    public double MaxDistanceFrom(GeoPoint from) =>
        IsCircle ? DedupScorer.DistanceMeters(from, Center!) + RadiusM!.Value
        : Rings.SelectMany(r => r).Select(p => DedupScorer.DistanceMeters(from, p)).DefaultIfEmpty(0).Max();

    // ------------------------------------------------------------------ calculs

    private const double MetersPerDegLat = 111_320;

    private static GeoPoint Offset(GeoPoint c, double dxM, double dyM) =>
        new(c.Lat + dyM / MetersPerDegLat, c.Lon + dxM / (MetersPerDegLat * Math.Cos(c.Lat * Math.PI / 180)));

    /// <summary>Lancer de rayon (lon = x, lat = y).</summary>
    private static bool InRing(List<GeoPoint> ring, GeoPoint p)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (xi, yi, xj, yj) = (ring[i].Lon, ring[i].Lat, ring[j].Lon, ring[j].Lat);
            if ((yi > p.Lat) != (yj > p.Lat) && p.Lon < (xj - xi) * (p.Lat - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>Distance du point au contour, en mètres (projection équirectangulaire locale).</summary>
    private static double DistanceToRing(List<GeoPoint> ring, GeoPoint p)
    {
        var kx = MetersPerDegLat * Math.Cos(p.Lat * Math.PI / 180);
        var best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double ax = (ring[j].Lon - p.Lon) * kx, ay = (ring[j].Lat - p.Lat) * MetersPerDegLat;
            double bx = (ring[i].Lon - p.Lon) * kx, by = (ring[i].Lat - p.Lat) * MetersPerDegLat;
            double dx = bx - ax, dy = by - ay;
            var len2 = dx * dx + dy * dy;
            var t = len2 == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / len2, 0, 1);
            double cx = ax + t * dx, cy = ay + t * dy;
            best = Math.Min(best, Math.Sqrt(cx * cx + cy * cy));
        }
        return best;
    }
}
