using System.Text.Json.Nodes;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Figaro;

/// <summary>
/// Recherche Figaro Immobilier par son serveur MCP public (<see cref="FigaroMcpClient"/>) :
///  - lieux : département direct ("d:65", sans appel) ; commune par l'outil "search_locations" (même nom, même code
///    postal ou même département : jamais une homonyme). Le site ne cherche pas par code postal.
///  - annonces : outil "listings", 20 annonces au plus par appel, les dernières mises à jour d'abord, sans page
///    suivante. Quand une zone en a plus de 20, la fourchette de prix est coupée en deux, puis encore en deux…
///    jusqu'à ce que chaque tranche tienne en 20 annonces (au plus <see cref="MaxCalls"/> appels par recherche).
///  - le site ne donne pas le code postal : il est repris de la commune cherchée, ou retrouvé d'après le nom de la
///    commune dans la liste officielle des communes du département (geo.api.gouv.fr).
/// </summary>
public sealed class FigaroSearchAdapter(FigaroMcpClient mcp, GeoServices geo) : ISearchAdapter
{
    /// <summary>Annonces par appel (fixé par le serveur MCP).</summary>
    public const int PageSize = 20;
    private const int MaxCalls = 60;
    /// <summary>Prix au-delà duquel la dernière tranche reste ouverte.</summary>
    private const decimal TopPrice = 3_000_000;
    /// <summary>Tranche de prix la plus fine (en dessous, les annonces de même prix ne se départagent plus).</summary>
    private const decimal MinBand = 2_000;

    public PlaceKinds SupportedPlaces => PlaceKinds.Commune | PlaceKinds.Department;

    public async Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct)
    {
        var text = query.Text.Trim();
        if (query.Kind == PlaceKind.Department)
            return GeoContours.DepartmentCode(text) is { } code ? new CachedPlace($"{text} ({code})", [$"d:{code}"]) : null;
        if (query.PostalCode is not { Length: 5 } cp) return null;
        var content = await mcp.CallToolAsync("search_locations", new JsonObject { ["locationName"] = text, ["postalCode"] = cp }, ct);
        return PickCommune(content, text, cp);
    }

    /// <summary>Ville (typeID 4 ou 6) du même nom, avec le même code postal ou, à défaut, du même département.</summary>
    public static CachedPlace? PickCommune(JsonNode? content, string commune, string cp)
    {
        var dep = ProjectScope.DepartmentOf(cp);
        var cities = (content?["locations"] as JsonArray ?? new JsonArray())
            .Where(l => JsonRead.Int(l?["typeID"]) is 4 or 6 && JsonRead.Str(l?["inseeCode"]) is not null)
            .Where(l => SameName(commune, JsonRead.Str(l?["cityLabel"]) ?? FigaroListingParser.SplitCity(JsonRead.Str(l?["cityName"])).City))
            .ToList();
        var city = cities.FirstOrDefault(l => JsonRead.Str(l?["postalCode"]) == cp)
            ?? cities.FirstOrDefault(l => dep.Contains(JsonRead.Str(l?["departmentCode"]) ?? ""));
        if (city is null) return null;
        return new CachedPlace($"{JsonRead.Str(city["cityLabel"]) ?? commune} ({cp})", [$"c:{JsonRead.Int(city["typeID"])}:{JsonRead.Str(city["inseeCode"])}:{cp}"]);
    }

    private static bool SameName(string a, string? b) =>
        b is not null && Core.Dedup.DedupScorer.NormalizeText(a) == Core.Dedup.DedupScorer.NormalizeText(b);

    public async Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var p = request.Project;
        var types = FigaroListingParser.SiteTypes(p.PropertyTypes);
        var items = new Dictionary<string, CollectedItem>(StringComparer.Ordinal);
        var complete = true;
        var total = 0;
        var calls = 0;

        foreach (var placeId in request.PlaceIds.Distinct())
        {
            if (Place(placeId) is not { } place) continue;
            // Tranches de prix à lire ; une tranche de plus de 20 annonces est coupée en deux.
            var bands = new Stack<(decimal? Min, decimal? Max)>();
            bands.Push((p.PriceMin, p.PriceMax));
            var placeTotal = -1;
            while (bands.Count > 0)
            {
                if (calls >= MaxCalls || items.Count >= request.MaxResults)
                {
                    complete = false;
                    break;
                }
                var (min, max) = bands.Pop();
                request.Report($"{place.Label}, {items.Count} annonce(s) lue(s)");
                calls++;
                var content = await mcp.CallToolAsync("listings", Arguments(p, place, types, min, max), ct);
                var (found, count) = FigaroListingParser.ParseListings(content, p.Transaction);
                if (placeTotal < 0) placeTotal = count;
                if (count > found.Count && Split(min, max) is { } halves)
                {
                    bands.Push(halves.High);
                    bands.Push(halves.Low);
                    continue; // les deux moitiés relisent ces annonces
                }
                if (count > found.Count) complete = false; // tranche trop fine pour tout lire
                foreach (var item in found)
                {
                    if (items.ContainsKey(item.SiteId)) continue;
                    items[item.SiteId] = new CollectedItem(item.SiteId, await WithPostalCodeAsync(item.Data, place, ct));
                }
            }
            total += Math.Max(placeTotal, 0);
        }
        return new SearchOutcome(items.Values.Take(request.MaxResults).ToList(), complete && items.Count <= request.MaxResults, total);
    }

    /// <summary>Lieu enregistré : département "d:65", ville "c:6:65031:65240" (typeID, INSEE, code postal).</summary>
    public sealed record FigaroPlace(int TypeId, string Code, string? PostalCode, string Label);

    public static FigaroPlace? Place(string id)
    {
        var parts = id.Split(':');
        return parts switch
        {
            ["d", var dep] => new FigaroPlace(3, dep, null, $"département {dep}"),
            ["c", var type, var insee, var cp] when int.TryParse(type, out var t) => new FigaroPlace(t, insee, cp, cp),
            _ => null,
        };
    }

    /// <summary>[min, max] -> deux moitiés ; sans maximum, d'abord jusqu'à <see cref="TopPrice"/>. Trop fine : null.</summary>
    public static ((decimal? Min, decimal? Max) Low, (decimal? Min, decimal? Max) High)? Split(decimal? min, decimal? max)
    {
        var lo = min ?? 0;
        if (max is null) return lo >= TopPrice ? null : ((min, TopPrice), (TopPrice + 1, null));
        if (max.Value - lo < MinBand) return null;
        var mid = Math.Floor((lo + max.Value) / 2);
        return ((min, mid), (mid + 1, max));
    }

    /// <summary>
    /// Arguments de l'outil "listings". Toujours : lieu, types, prix, surface, pièces. Seulement si obligatoires (le
    /// site écarte les annonces sans l'information) : chambres, DPE, équipements. Terrain, année, travaux : l'outil
    /// ne sait pas les filtrer, ils sont vérifiés par l'application.
    /// </summary>
    public static JsonObject Arguments(SearchProject p, FigaroPlace place, IReadOnlyList<string> types, decimal? min, decimal? max)
    {
        var a = new JsonObject
        {
            ["typeID"] = place.TypeId,
            ["transactionType"] = p.Transaction == TransactionType.Buy ? "vente" : "location",
        };
        if (place.TypeId == 3) a["legacyID"] = place.Code;
        else a["insee"] = place.Code;
        if (types.Count > 0) a["estateTypes"] = new JsonArray([.. types.Select(t => JsonValue.Create(t))]);
        if (min is { } pmin) a["minimumPrice"] = (long)pmin;
        if (max is { } pmax) a["maximumPrice"] = (long)pmax;
        if (p.SurfaceMin is { } smin) a["minimumSurface"] = (int)Math.Floor(smin);
        if (p.SurfaceMax is { } smax) a["maximumSurface"] = (int)Math.Ceiling(smax);
        if (p.RoomsMin is { } rmin) a["minimumRooms"] = rmin;
        if (p.RoomsMax is { } rmax) a["maximumRooms"] = rmax;
        if (p.IsStrict(Criteria.Bedrooms) && p.BedroomsMin is { } bmin) a["minimumBedrooms"] = bmin;
        if (p.IsStrict(Criteria.EnergyClass) && p.EnergyClassMax is { } dpe)
            a["dpeCategories"] = new JsonArray([.. EnergyClasses.UpTo(dpe).Select(c => JsonValue.Create(c))]);
        if (p.IsStrict(Criteria.Features))
        {
            var options = p.RequiredFeatures.Select(Option).OfType<string>().Distinct().ToList();
            if (options.Count > 0) a["options"] = new JsonArray([.. options.Select(o => JsonValue.Create(o))]);
        }
        return a;
    }

    private static string? Option(string feature) => feature switch
    {
        Features.Garden => "jardin",
        Features.Pool => "piscine",
        Features.Terrace => "terrasse",
        Features.Garage => "garage",
        Features.Cellar => "cave",
        Features.Elevator => "ascenseur",
        Features.Fireplace => "cheminee",
        Features.AirConditioning => "climatisation",
        _ => null, // balcon, parking, accès PMR : pas d'option de recherche, vérifiés par l'application
    };

    /// <summary>Code postal : celui de la commune cherchée, sinon d'après le nom de la commune dans le département.</summary>
    private async Task<ListingData> WithPostalCodeAsync(ListingData d, FigaroPlace place, CancellationToken ct)
    {
        if (place.PostalCode is { } cp) return d with { PostalCode = cp };
        if (d.City is null) return d;
        try
        {
            var communes = await geo.CommunesAsync(place.Code, ct);
            var commune = communes.FirstOrDefault(c => SameName(c.Name, d.City))
                ?? communes.FirstOrDefault(c => ProjectScope.SameCommune(c.Name, d.City));
            return commune?.PostalCodes.FirstOrDefault() is { } found ? d with { PostalCode = found } : d;
        }
        catch (SiteException)
        {
            return d; // service indisponible : l'annonce est gardée sans code postal
        }
    }
}
