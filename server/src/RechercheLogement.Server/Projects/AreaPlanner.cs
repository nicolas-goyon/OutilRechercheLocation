using RechercheLogement.Core.Geo;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites;

namespace RechercheLogement.Server.Projects;

/// <summary>
/// Calcule la zone d'un projet en mode rayon ou temps de trajet :
///  1. point de départ (choisi sur la carte / dans les suggestions, sinon géocodage du texte saisi) ;
///  2. forme exacte : cercle, ou isochrone (Valhalla/OSM) ; si l'isochrone échoue, cercle estimé avec une
///     vitesse moyenne (zone marquée "approchée") ;
///  3. départements touchés (centre + points du bord) et communes de ces départements, marquées "dans la
///     zone" si leur centre y est (marge 1,5 km).
/// Le résultat est gardé dans le projet et recalculé si la zone change ou tous les 30 jours.
/// </summary>
public sealed class AreaPlanner(GeoServices geo)
{
    private const long MaxAgeMs = 30L * 24 * 3_600_000;
    private const double CommuneMarginM = 1500;

    /// <summary>Vitesse moyenne "à vol d'oiseau" pour estimer une zone sans isochrone (km par minute).</summary>
    private static double CrowFliesKmPerMinute(TravelMode m) => m switch { TravelMode.Walk => 0.07, TravelMode.Bike => 0.22, _ => 0.75 };

    public static bool NeedsPlan(SearchProject p) => p.LocationMode is LocationMode.Radius or LocationMode.TravelTime;

    public static bool IsFresh(SearchProject p, long now) =>
        p.Area is { } a && a.Fingerprint == p.AreaFingerprint() && now - a.ComputedAt < MaxAgeMs && a.Communes.Count > 0;

    public async Task<AreaPlan> BuildAsync(SearchProject p, long now, CancellationToken ct)
    {
        GeocodedPlace place;
        if (p.Center is { } chosen)
        {
            // Point choisi dans les suggestions ou sur la carte (ou déjà géocodé) : pas de nouveau géocodage.
            place = new GeocodedPlace(p.CenterLabel ?? p.CenterQuery ?? "point choisi", chosen);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(p.CenterQuery)) throw new SiteException("Point de départ manquant (adresse ou ville).");
            place = await geo.GeocodeAsync(p.CenterQuery, ct) ?? throw new SiteException($"Adresse introuvable : « {p.CenterQuery} ».");
            p.Center = place.Point;
            p.CenterLabel = place.Label;
        }

        var plan = new AreaPlan { Fingerprint = p.AreaFingerprint(), ComputedAt = now };
        if (p.LocationMode == LocationMode.Radius)
        {
            plan.Shape = GeoShape.Circle(place.Point, p.RadiusKm * 1000);
            plan.Note = $"{p.RadiusKm:0.#} km autour de {place.Label}";
        }
        else
        {
            var by = p.TravelBy switch { TravelMode.Bike => "à vélo", TravelMode.Walk => "à pied", _ => "en voiture" };
            try
            {
                var iso = await geo.IsochroneAsync(place.Point, p.TravelMinutes, p.TravelBy, ct);
                // Le centre sert au calcul des distances affichées.
                plan.Shape = new GeoShape { Rings = iso.Rings, Center = place.Point };
                plan.Note = $"{p.TravelMinutes} min {by} depuis {place.Label} (isochrone OpenStreetMap)";
            }
            catch (SiteException e)
            {
                plan.Shape = GeoShape.Circle(place.Point, p.TravelMinutes * CrowFliesKmPerMinute(p.TravelBy) * 1000);
                plan.Approximate = true;
                plan.Note = $"{p.TravelMinutes} min {by} depuis {place.Label} : zone estimée (cercle de {plan.Shape.RadiusM / 1000:0} km), isochrone indisponible ({e.Message})";
            }
        }

        // Départements touchés : centre + points du bord de la zone.
        var departments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pt in plan.Shape.BoundaryPoints(24).Prepend(place.Point))
        {
            if (await geo.DepartmentAtAsync(pt, ct) is { } dep) departments.Add(dep);
        }
        if (departments.Count == 0) throw new SiteException("Zone hors de France : aucun département trouvé.");

        foreach (var dep in departments.Order(StringComparer.Ordinal))
        {
            plan.Departments[dep] = await geo.DepartmentNameAsync(dep, ct);
            foreach (var c in await geo.CommunesAsync(dep, ct))
                plan.Communes.Add(c with { InArea = plan.Shape.Contains(c.Center, CommuneMarginM) });
        }
        return plan;
    }
}
