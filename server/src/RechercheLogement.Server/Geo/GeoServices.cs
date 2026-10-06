using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Geo;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Geo;

/// <summary>Adresse ou ville trouvée par le géocodage.</summary>
public sealed record GeocodedPlace(string Label, GeoPoint Point);

/// <summary>
/// Suggestion affichée sous un champ de saisie. <paramref name="Location"/> : lieu à ajouter (mode lieux) ;
/// <paramref name="Point"/> : point de départ (modes rayon et temps de trajet).
/// </summary>
public sealed record PlaceSuggestion(string Label, string? Detail, string Icon, ProjectLocation? Location, GeoPoint? Point);

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

    // ------------------------------------------------------------------ suggestions (saisie de la zone)

    private const string CommuneFields = "fields=nom,code,codesPostaux,centre,codeDepartement";

    /// <summary>
    /// Communes, codes postaux et départements correspondant au début de saisie : "rod" -> Rodez, Rodelle… ;
    /// "12850" -> le code postal et ses communes ; "12" ou "avey" -> le département.
    /// </summary>
    public async Task<List<PlaceSuggestion>> SuggestPlacesAsync(string text, CancellationToken ct)
    {
        text = text.Trim();
        var list = new List<PlaceSuggestion>();
        if (text.Length == 0) return list;
        if (Regex.IsMatch(text, @"^\d{5}$"))
        {
            var communes = ParseCommunes(await GetGeoAsync($"communes?codePostal={text}&{CommuneFields}", ct));
            if (communes.Count > 0) list.Add(PostalCodeSuggestion(text, communes));
            list.AddRange(communes.Take(6).Select(CommuneSuggestion));
            return list;
        }
        if (Regex.IsMatch(text, @"^(\d{1,4}|2[ABab])$"))
        {
            // Début de code postal ou code de département.
            if (text.Length <= 3 && await DepartmentByCodeAsync(text, ct) is { } dep) list.Add(dep);
            return list;
        }
        var q = Uri.EscapeDataString(text);
        var deps = ParseDepartments(await GetGeoAsync($"departements?nom={q}&fields=nom,code&limit=3", ct));
        var towns = ParseCommunes(await GetGeoAsync($"communes?nom={q}&{CommuneFields}&boost=population&limit=7", ct));
        // Département en tête seulement si son nom correspond mieux que la première commune ("Aveyron" et non "Paris").
        var depFirst = deps.Count > 0 && DedupScorer.NormalizeText(deps[0].Label).StartsWith(DedupScorer.NormalizeText(text), StringComparison.Ordinal);
        if (depFirst) list.AddRange(deps.Take(1));
        list.AddRange(towns.Select(CommuneSuggestion));
        list.AddRange(deps.Skip(depFirst ? 1 : 0));
        return list;
    }

    /// <summary>Commune qui contient le point (clic sur la carte en mode lieux).</summary>
    public async Task<PlaceSuggestion?> CommuneAtAsync(GeoPoint p, CancellationToken ct)
    {
        var json = await GetGeoAsync(string.Create(CultureInfo.InvariantCulture, $"communes?lat={p.Lat}&lon={p.Lon}&{CommuneFields}"), ct);
        return ParseCommunes(json).Select(CommuneSuggestion).FirstOrDefault();
    }

    /// <summary>Adresses, lieux-dits et villes pour le point de départ (autocomplétion IGN, repli api-adresse).</summary>
    public async Task<List<PlaceSuggestion>> SuggestAddressesAsync(string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length < 3) return [];
        var q = Uri.EscapeDataString(text);
        JsonNode json;
        try
        {
            json = await SiteHttp.GetJsonAsync(Http, $"https://data.geopf.fr/geocodage/search?q={q}&autocomplete=1&limit=6", "Géocodage IGN", ct);
        }
        catch (SiteException)
        {
            json = await SiteHttp.GetJsonAsync(Http, $"https://api-adresse.data.gouv.fr/search/?q={q}&autocomplete=1&limit=6", "Géocodage (api-adresse)", ct);
        }
        return ParseAddresses(json);
    }

    /// <summary>Adresse la plus proche du point (clic sur la carte). null : rien à proximité.</summary>
    public async Task<GeocodedPlace?> ReverseAsync(GeoPoint p, CancellationToken ct)
    {
        var args = string.Create(CultureInfo.InvariantCulture, $"lon={p.Lon}&lat={p.Lat}&limit=1");
        JsonNode json;
        try
        {
            json = await SiteHttp.GetJsonAsync(Http, $"https://data.geopf.fr/geocodage/reverse?{args}", "Géocodage IGN", ct);
        }
        catch (SiteException)
        {
            json = await SiteHttp.GetJsonAsync(Http, $"https://api-adresse.data.gouv.fr/reverse/?{args}", "Géocodage (api-adresse)", ct);
        }
        var label = ParseAddresses(json).FirstOrDefault()?.Label;
        return label is null ? null : new GeocodedPlace(label, p);
    }

    private async Task<PlaceSuggestion?> DepartmentByCodeAsync(string code, CancellationToken ct)
    {
        if (code.Length == 1) code = "0" + code;
        try
        {
            var json = await GetGeoAsync($"departements/{code.ToUpperInvariant()}?fields=nom,code", ct);
            return ParseDepartments(new JsonArray(json.DeepClone())).FirstOrDefault();
        }
        catch (SiteException)
        {
            return null; // 404 : pas de département avec ce code.
        }
    }

    private Task<JsonNode> GetGeoAsync(string path, CancellationToken ct) =>
        SiteHttp.GetJsonAsync(Http, $"https://geo.api.gouv.fr/{path}", "geo.api.gouv.fr", ct);

    /// <summary>Réponse geo.api.gouv.fr/communes -> communes (avec centre).</summary>
    public static List<PlanCommune> ParseCommunes(JsonNode? json)
    {
        var list = new List<PlanCommune>();
        foreach (var c in json as JsonArray ?? new JsonArray())
        {
            var coords = c?["centre"]?["coordinates"] as JsonArray;
            if (c is null || coords is not { Count: >= 2 }) continue;
            list.Add(new PlanCommune(
                JsonRead.Str(c["code"]) ?? "",
                JsonRead.Str(c["nom"]) ?? "",
                JsonRead.Strings(c["codesPostaux"]),
                JsonRead.Str(c["codeDepartement"]) ?? "",
                new GeoPoint(JsonRead.Dbl(coords[1]) ?? 0, JsonRead.Dbl(coords[0]) ?? 0),
                false));
        }
        return list;
    }

    /// <summary>Réponse geo.api.gouv.fr/departements -> suggestions de départements.</summary>
    public static List<PlaceSuggestion> ParseDepartments(JsonNode? json) =>
        (json as JsonArray ?? new JsonArray())
            .Select(d => (Name: JsonRead.Str(d?["nom"]), Code: JsonRead.Str(d?["code"])))
            .Where(d => d.Name is not null && d.Code is not null)
            .Select(d => new PlaceSuggestion($"{d.Name} ({d.Code})", "Département", "🗺",
                new ProjectLocation { Type = PlaceType.Department, Query = d.Name!, Code = d.Code, Label = $"{d.Name} ({d.Code})" }, null))
            .ToList();

    public static PlaceSuggestion CommuneSuggestion(PlanCommune c)
    {
        var cps = c.PostalCodes.Count switch { 0 => "", 1 => c.PostalCodes[0], _ => $"{c.PostalCodes[0]}…" };
        var label = cps.Length == 0 ? c.Name : $"{c.Name} ({cps})";
        var detail = c.PostalCodes.Count > 1 ? $"Commune · codes postaux {string.Join(", ", c.PostalCodes)}" : $"Commune · département {c.Department}";
        return new PlaceSuggestion(label, detail, "🏘",
            new ProjectLocation { Type = PlaceType.Commune, Query = c.Name, Code = c.Code, Label = label, PostalCodes = [.. c.PostalCodes], Center = c.Center },
            c.Center);
    }

    public static PlaceSuggestion PostalCodeSuggestion(string cp, List<PlanCommune> communes)
    {
        var names = string.Join(", ", communes.Take(4).Select(c => c.Name)) + (communes.Count > 4 ? $" et {communes.Count - 4} autre(s)" : "");
        return new PlaceSuggestion(cp, $"Code postal · {names}", "📮",
            new ProjectLocation { Type = PlaceType.PostalCode, Query = cp, Code = cp, Label = $"{cp} ({communes[0].Name}{(communes.Count > 1 ? "…" : "")})", Center = communes[0].Center },
            communes[0].Center);
    }

    /// <summary>Réponse GeoJSON du géocodage (IGN ou api-adresse) -> suggestions de points de départ.</summary>
    public static List<PlaceSuggestion> ParseAddresses(JsonNode? json)
    {
        var list = new List<PlaceSuggestion>();
        foreach (var f in json?["features"] as JsonArray ?? new JsonArray())
        {
            var coords = f?["geometry"]?["coordinates"] as JsonArray;
            var label = JsonRead.Str(f?["properties"]?["label"]);
            if (coords is not { Count: >= 2 } || label is null) continue;
            var type = JsonRead.Str(f!["properties"]?["type"]);
            (string Icon, string? Kind) meta = type switch
            {
                "housenumber" => ("📍", "Adresse"),
                "street" => ("🛣", "Voie"),
                "municipality" => ("🏘", "Commune"),
                "locality" => ("📌", "Lieu-dit"),
                _ => ("📍", null),
            };
            var context = JsonRead.Str(f["properties"]?["context"]);
            var detail = meta.Kind is null ? context : context is null ? meta.Kind : $"{meta.Kind} · {context}";
            list.Add(new PlaceSuggestion(label, detail, meta.Icon, null, new GeoPoint(JsonRead.Dbl(coords[1]) ?? 0, JsonRead.Dbl(coords[0]) ?? 0)));
        }
        return list;
    }

    // ------------------------------------------------------------------ zone (modes rayon / temps de trajet)

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
        var list = ParseCommunes(json).Select(c => c with { Department = department }).ToList();
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
