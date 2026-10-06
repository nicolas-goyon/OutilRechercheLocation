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
        var http = factory.CreateClient(SiteHttp.ClientName);
        var json = await SiteHttp.GetJsonAsync(http, $"https://res.bienici.com/suggest.json?q={Uri.EscapeDataString(query.Text.Trim())}", Label, ct);
        return PickPlace(json, query);
    }

    /// <summary>
    /// Choix dans les suggestions : département demandé -> type "department" ; code postal -> lieu qui a ce code ;
    /// texte libre -> premier lieu avec des zoneIds (un code postal saisi, ou celui de la commune choisie, doit figurer dans le lieu).
    /// </summary>
    public static CachedPlace? PickPlace(JsonNode json, PlaceQuery query)
    {
        if (json is not JsonArray places) return null;
        var text = query.Text.Trim();
        if (Regex.IsMatch(text, @"^\d{5}$")) return Pick(places, query, text);
        // Commune choisie dans les suggestions : on préfère le lieu qui a son code postal, sinon le premier.
        return (query.PostalCode is null ? null : Pick(places, query, query.PostalCode)) ?? Pick(places, query, null);
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

    /// <summary>Filtres de l'API (mêmes noms que le site : minPrice, maxArea, minRooms...).</summary>
    public static JsonObject BuildFilters(SearchProject p, IReadOnlyList<string> zoneIds, int page)
    {
        var f = new JsonObject
        {
            ["size"] = PageSize,
            ["from"] = page * PageSize,
            ["page"] = page + 1,
            ["filterType"] = p.Transaction == TransactionType.Buy ? "buy" : "rent",
            ["propertyType"] = new JsonArray(p.PropertyTypes.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
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
        return f;
    }
}
