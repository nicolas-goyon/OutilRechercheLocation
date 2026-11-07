using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Bienici;

/// <summary>
/// Recherche Bien'ici, sans compte :
///  - lieux : https://res.bienici.com/suggest.json?q=… -> zoneIds (ville, code postal, département, région) ;
///  - annonces : https://www.bienici.com/realEstateAds.json?filters={…}, 100 par page, plus récentes d'abord.
/// Pas de recherche par rayon ou zone dessinée : le lanceur passe par les codes postaux ou départements.
/// </summary>
public sealed class BieniciSearchAdapter(IHttpClientFactory factory) : ISearchAdapter
{
    private const string Label = "Bien'ici";
    private const int PageSize = 100;
    /// <summary>Lieux par requête : la liste des zoneIds part dans l'URL.</summary>
    private const int MaxPlacesPerQuery = 60;

    public PlaceKinds SupportedPlaces => PlaceKinds.PostalCode | PlaceKinds.Commune | PlaceKinds.Department | PlaceKinds.Region;

    public async Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct)
    {
        var place = PickPlace(await SuggestAsync(query.Text, ct), query);
        if (place is not null || query is not { Kind: PlaceKind.Auto, PostalCode: { Length: 5 } cp }) return place;
        // Bien'ici ne propose que 10 lieux : pour un nom courant (Saint-Michel, Villeneuve...), la bonne commune n'y
        // est souvent pas. Avec le code postal, elle passe en tête.
        place = PickPlace(await SuggestAsync($"{query.Text.Trim()} {cp}", ct), query);
        if (place is not null) return place;
        // Commune inconnue de Bien'ici (commune nouvelle : Val-de-Sos, découpée en ses anciennes communes) : tout son
        // code postal. Les annonces des autres communes de ce code postal sont écartées par le filtre du projet.
        var zone = PickPlace(await SuggestAsync(cp, ct), new PlaceQuery(PlaceKind.PostalCode, cp));
        return zone is null ? null : new CachedPlace($"{query.Text.Trim()} (tout le code postal {cp})", zone.Ids);
    }

    private async Task<JsonNode> SuggestAsync(string text, CancellationToken ct) =>
        await SiteHttp.GetJsonAsync(factory.CreateClient(SiteHttp.ClientName),
            $"https://res.bienici.com/suggest.json?q={Uri.EscapeDataString(text.Trim())}", Label, ct);

    /// <summary>
    /// Choix dans les suggestions : département demandé -> type "department" ; code postal -> lieu qui a ce code ;
    /// texte libre -> premier lieu avec des zoneIds ; commune choisie -> lieu de son code postal ou de son département.
    /// </summary>
    public static CachedPlace? PickPlace(JsonNode json, PlaceQuery query)
    {
        if (json is not JsonArray places) return null;
        var text = query.Text.Trim();
        if (Regex.IsMatch(text, @"^\d{5}$"))
            return Pick(new JsonArray(places.Where(p => JsonRead.Str(p?["type"]) == "postalCode").Select(p => p?.DeepClone()).ToArray()), query, text)
                ?? Pick(places, query, text);
        // Commune choisie dans les suggestions : le lieu qui a son code postal, ou à défaut une commune du même nom dans
        // le même département. Jamais une homonyme ailleurs : la recherche partirait dans une autre région.
        if (query.PostalCode is not { Length: 5 } cp) return Pick(places, query, null);
        return Pick(places, query, cp) ?? PickSameDepartment(places, query, cp);
    }

    private static CachedPlace? PickSameDepartment(JsonArray places, PlaceQuery query, string cp)
    {
        var name = DedupScorer.NormalizeText(query.Text);
        foreach (var place in places)
        {
            if (place is null || DedupScorer.NormalizeText(JsonRead.Str(place["name"]) ?? "") != name) continue;
            var postalCodes = JsonRead.Strings(place["postalCodes"]);
            var zones = JsonRead.Strings(place["zoneIds"]);
            if (zones.Count > 0 && postalCodes.Any(x => x.Length == 5 && x[..2] == cp[..2]))
                return new CachedPlace($"{JsonRead.Str(place["name"])} ({postalCodes[0]})", zones);
        }
        return null;
    }

    private static CachedPlace? Pick(JsonArray places, PlaceQuery query, string? cp)
    {
        var text = query.Text.Trim();
        foreach (var place in places)
        {
            if (place is null) continue;
            var zones = JsonRead.Strings(place["zoneIds"]);
            if (zones.Count == 0) continue;
            var type = JsonRead.Str(place["type"]);
            var postalCodes = JsonRead.Strings(place["postalCodes"]);
            var name = JsonRead.Str(place["name"]) ?? text;
            if (query.Kind == PlaceKind.Department && (type != "department" || DedupScorer.NormalizeText(name) != DedupScorer.NormalizeText(text))) continue;
            if (cp is not null && !postalCodes.Contains(cp)) continue;
            // Commune : le même nom, pas une autre commune du même code postal ("Orus" pour "Val-de-Sos 09220").
            if (query is { Kind: PlaceKind.Auto, PostalCode: not null } && !ProjectScope.SameCommune(text, name)) continue;
            var label = type == "department" ? $"{name} (département)" : postalCodes.Count == 1 && name != postalCodes[0] ? $"{name} ({postalCodes[0]})" : name;
            return new CachedPlace(label, zones);
        }
        return null;
    }

    public async Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var http = factory.CreateClient(SiteHttp.ClientName);
        var items = new List<CollectedItem>();
        var complete = true;
        var total = 0;
        var maxPages = Math.Max(1, (int)Math.Ceiling(request.MaxResults / (double)PageSize));
        foreach (var chunk in request.PlaceIds.Distinct().Chunk(MaxPlacesPerQuery))
        {
            for (var page = 0; ; page++)
            {
                request.Report($"page {page + 1}, {items.Count} annonce(s) lue(s)");
                var filters = BuildFilters(request.Project, chunk, page);
                var url = $"https://www.bienici.com/realEstateAds.json?filters={Uri.EscapeDataString(filters.ToJsonString())}";
                var json = await SiteHttp.GetJsonAsync(http, url, Label, ct);
                var ads = json["realEstateAds"] as JsonArray ?? new JsonArray();
                foreach (var ad in ads)
                    if (ad is not null && BieniciListingParser.ParseAd(ad) is { } item) items.Add(item);
                var chunkTotal = JsonRead.Int(json["total"]) ?? 0;
                if (ads.Count < PageSize || (page + 1) * PageSize >= chunkTotal)
                {
                    total += chunkTotal;
                    break;
                }
                if (page + 1 >= maxPages)
                {
                    total += chunkTotal;
                    complete = false; // il reste des annonces plus anciennes
                    break;
                }
                await Task.Delay(800, ct); // une requête à la fois, sans insister
            }
        }
        return new SearchOutcome(items, complete, total);
    }

    /// <summary>Types communs -> valeurs "propertyType" de Bien'ici (relevées sur l'API en novembre 2026).</summary>
    public static IEnumerable<string> SiteTypes(IEnumerable<string> types) => types.Select(PropertyTypes.Normalize).SelectMany(t => t switch
    {
        PropertyTypes.Flat => ["flat"],
        PropertyTypes.House => ["house"],
        PropertyTypes.TownHouse => ["townhouse"],
        PropertyTypes.Castle => ["castle"],
        PropertyTypes.Loft => ["loft"],
        PropertyTypes.Terrain => ["terrain"],
        PropertyTypes.Building => ["building"],
        PropertyTypes.Commercial => ["shop", "premises", "office"],
        PropertyTypes.Parking => ["parking"],
        PropertyTypes.Other => ["others"],
        _ => Array.Empty<string>(),
    }).Distinct();

    /// <summary>Filtres de l'API (mêmes noms que le site : minPrice, maxArea, minRooms...).</summary>
    public static JsonObject BuildFilters(SearchProject p, IReadOnlyList<string> zoneIds, int page)
    {
        var f = new JsonObject
        {
            ["size"] = PageSize,
            ["from"] = page * PageSize,
            ["page"] = page + 1,
            ["filterType"] = p.Transaction == TransactionType.Buy ? "buy" : "rent",
            ["propertyType"] = new JsonArray(SiteTypes(p.PropertyTypes).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["sortBy"] = "publicationDate",
            ["sortOrder"] = "desc",
            ["onTheMarket"] = new JsonArray(true),
            ["zoneIdsByTypes"] = new JsonObject { ["zoneIds"] = new JsonArray(zoneIds.Select(z => (JsonNode?)JsonValue.Create(z)).ToArray()) },
        };
        if (p.PriceMin is { } pmin) f["minPrice"] = pmin;
        if (p.PriceMax is { } pmax) f["maxPrice"] = pmax;
        if (p.SurfaceMin is { } smin) f["minArea"] = smin;
        if (p.SurfaceMax is { } smax) f["maxArea"] = smax;
        if (p.RoomsMin is { } rmin) f["minRooms"] = rmin;
        if (p.RoomsMax is { } rmax) f["maxRooms"] = rmax;
        if (p.BedroomsMin is { } bmin) f["minBedrooms"] = bmin;
        if (p.Furnished is { } furnished) f["isFurnished"] = furnished;
        if (p.LandSurfaceMin is { } lmin) f["minGardenSurfaceArea"] = lmin;
        if (p.LandSurfaceMax is { } lmax) f["maxGardenSurfaceArea"] = lmax;
        if (p.EnergyClassMax is { } energy) f["energyClassification"] = new JsonArray(EnergyClasses.UpTo(energy).Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        if (p.NewBuild is { } newBuild) f["newProperty"] = newBuild;
        // workToDo: false ne garde que les annonces qui le disent : "sans travaux" est vérifié par l'outil.
        if (p.WorkNeeded == true) f["workToDo"] = true;
        foreach (var name in SiteFeatures(p.RequiredFeatures)) f[name] = true;
        return f;
    }

    /// <summary>
    /// Équipements communs -> filtres de l'API (relevés en novembre 2026). Garage et parking : "hasParking" (pas de
    /// filtre garage seul). Climatisation : pas de filtre, vérifiée par l'outil sur les annonces.
    /// </summary>
    public static IEnumerable<string> SiteFeatures(IEnumerable<string> features) => features.Select(f => f switch
    {
        Features.Garden => "hasGarden",
        Features.Pool => "hasPool",
        Features.Terrace => "hasTerrace",
        Features.Balcony => "hasBalcony",
        Features.Cellar => "hasCellar",
        Features.Elevator => "hasElevator",
        Features.Fireplace => "hasFirePlace",
        Features.Garage or Features.Parking => "hasParking",
        Features.Accessible => "isDisabledPeopleFriendly",
        _ => null,
    }).OfType<string>().Distinct();
}
