using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using RechercheLogement.Core.Geo;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Geo;

/// <summary>Adresse ou ville trouvée par le géocodage.</summary>
public sealed record GeocodedPlace(string Label, GeoPoint Point);

/// <summary>
/// Services géographiques publics et gratuits, sans clé :
///  - géocodage : Géoplateforme IGN (data.geopf.fr/geocodage), repli sur api-adresse.data.gouv.fr ;
///  - communes et départements : geo.api.gouv.fr (centre de chaque commune, codes postaux) ;
///  - isochrones (temps de trajet) : Valhalla sur OpenStreetMap (valhalla1.openstreetmap.de).
/// </summary>
public sealed class GeoServices(IHttpClientFactory factory)
{
    private readonly ConcurrentDictionary<string, List<PlanCommune>> _communes = new();
    private readonly ConcurrentDictionary<string, string> _departmentNames = new();

    private HttpClient Http => factory.CreateClient(SiteHttp.ClientName);

    /// <summary>Adresse, ville ou code postal -> point. null si rien trouvé.</summary>
    public async Task<GeocodedPlace?> GeocodeAsync(string text, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(text.Trim());
        JsonNode json;
        try
        {
            json = await SiteHttp.GetJsonAsync(Http, $"https://data.geopf.fr/geocodage/search?q={q}&limit=1", "Géocodage IGN", ct);
        }
        catch (SiteException)
        {
            json = await SiteHttp.GetJsonAsync(Http, $"https://api-adresse.data.gouv.fr/search/?q={q}&limit=1", "Géocodage (api-adresse)", ct);
        }
        var feature = (json["features"] as JsonArray)?.FirstOrDefault();
        var coords = feature?["geometry"]?["coordinates"] as JsonArray;
        if (coords is not { Count: >= 2 }) return null;
        var label = JsonRead.Str(feature!["properties"]?["label"]) ?? text;
        return new GeocodedPlace(label, new GeoPoint(JsonRead.Dbl(coords[1]) ?? 0, JsonRead.Dbl(coords[0]) ?? 0));
    }

    /// <summary>Code du département qui contient le point (null : hors de France, en mer...).</summary>
    public async Task<string?> DepartmentAtAsync(GeoPoint p, CancellationToken ct)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"https://geo.api.gouv.fr/communes?lat={p.Lat}&lon={p.Lon}&fields=codeDepartement");
        var json = await SiteHttp.GetJsonAsync(Http, url, "geo.api.gouv.fr", ct);
        return JsonRead.Str((json as JsonArray)?.FirstOrDefault()?["codeDepartement"]);
    }

    public async Task<string> DepartmentNameAsync(string code, CancellationToken ct)
    {
        if (_departmentNames.TryGetValue(code, out var name)) return name;
        var json = await SiteHttp.GetJsonAsync(Http, $"https://geo.api.gouv.fr/departements/{code}?fields=nom", "geo.api.gouv.fr", ct);
        name = JsonRead.Str(json["nom"]) ?? code;
        _departmentNames[code] = name;
        return name;
    }

    /// <summary>Toutes les communes d'un département, avec leur centre (gardées en mémoire).</summary>
    public async Task<List<PlanCommune>> CommunesAsync(string department, CancellationToken ct)
    {
        if (_communes.TryGetValue(department, out var cached)) return cached;
        var json = await SiteHttp.GetJsonAsync(Http, $"https://geo.api.gouv.fr/departements/{department}/communes?fields=nom,code,codesPostaux,centre", "geo.api.gouv.fr", ct);
        var list = new List<PlanCommune>();
        foreach (var c in json as JsonArray ?? new JsonArray())
        {
            var coords = c?["centre"]?["coordinates"] as JsonArray;
            if (c is null || coords is not { Count: >= 2 }) continue;
            list.Add(new PlanCommune(
                JsonRead.Str(c["code"]) ?? "",
                JsonRead.Str(c["nom"]) ?? "",
                JsonRead.Strings(c["codesPostaux"]),
                department,
                new GeoPoint(JsonRead.Dbl(coords[1]) ?? 0, JsonRead.Dbl(coords[0]) ?? 0),
                false));
        }
        _communes[department] = list;
        return list;
    }

    /// <summary>Zone atteignable en <paramref name="minutes"/> depuis le point (contours extérieurs de l'isochrone).</summary>
    public async Task<GeoShape> IsochroneAsync(GeoPoint from, int minutes, TravelMode mode, CancellationToken ct)
    {
        var costing = mode switch { TravelMode.Bike => "bicycle", TravelMode.Walk => "pedestrian", _ => "auto" };
        var body = new JsonObject
        {
            ["locations"] = new JsonArray(new JsonObject { ["lat"] = from.Lat, ["lon"] = from.Lon }),
            ["costing"] = costing,
            ["contours"] = new JsonArray(new JsonObject { ["time"] = minutes }),
            ["polygons"] = true,
            ["denoise"] = 0.5,
            ["generalize"] = 150,
        };
        var url = $"https://valhalla1.openstreetmap.de/isochrone?json={Uri.EscapeDataString(body.ToJsonString())}";
        var json = await SiteHttp.GetJsonAsync(Http, url, "Calcul d'isochrone (Valhalla/OSM)", ct);
        var geometry = (json["features"] as JsonArray)?.FirstOrDefault()?["geometry"];
        var type = JsonRead.Str(geometry?["type"]);
        var polygons = new List<JsonNode?>();
        if (type == "Polygon") polygons.Add(geometry!["coordinates"]);
        else if (type == "MultiPolygon" && geometry!["coordinates"] is JsonArray multi) polygons.AddRange(multi);
        // Pour chaque polygone, seul le contour extérieur (premier anneau) compte.
        var rings = polygons
            .Select(poly => (poly as JsonArray)?.FirstOrDefault() as JsonArray)
            .Where(ring => ring is not null)
            .Select(ring => ring!.OfType<JsonArray>()
                .Where(pt => pt.Count >= 2)
                .Select(pt => new GeoPoint(JsonRead.Dbl(pt[1]) ?? 0, JsonRead.Dbl(pt[0]) ?? 0))
                .ToList())
            .ToList();
        var shape = GeoShape.Polygons(rings);
        if (shape.Rings.Count == 0) throw new SiteException("Calcul d'isochrone : réponse sans zone.");
        return shape;
    }
}
