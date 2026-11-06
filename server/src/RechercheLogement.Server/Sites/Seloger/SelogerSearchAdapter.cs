using System.Text.Json.Nodes;
using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Seloger;

/// <summary>
/// Recherche SeLoger :
///  - lieux : https://www.seloger.com/search-mfe-bff/autocomplete/suggestion?query=… -> placeIds
///    (ville AD08…, code postal POCO…, département AD06…) ;
///  - annonces : POST https://www.seloger.com/serp-bff/search (identifiants, 30 par page, plus récentes
///    d'abord), puis GET https://www.seloger.com/classifiedList/&lt;ids&gt; (données des annonces).
/// SeLoger refuse les appels hors navigateur : ils passent par le plugin (<see cref="BrowserRelay"/>), dans un
/// onglet seloger.com ouvert par l'utilisateur. Sans onglet ouvert, la recherche attend (message affiché).
/// </summary>
public sealed class SelogerSearchAdapter(BrowserRelay relay) : ISearchAdapter
{
    private const string Label = "SeLoger";
    private const int PageSize = 30;
    private const int MaxPlacesPerQuery = 50;

    public PlaceKinds SupportedPlaces => PlaceKinds.PostalCode | PlaceKinds.Commune | PlaceKinds.Department | PlaceKinds.Region;

    public async Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct)
    {
        var url = $"https://www.seloger.com/search-mfe-bff/autocomplete/suggestion?query={Uri.EscapeDataString(query.Text.Trim())}&limit=5&distributionType={Distribution(transaction)}";
        var json = await GetAsync(url, ct);
        return PickPlace(json, query);
    }

    /// <summary>
    /// Premier lieu proposé. Département : le libellé doit être le nom du département. Commune choisie dans les
    /// suggestions : le libellé doit contenir son code postal ou son département ("Rodez (12)"), sinon le lieu n'est
    /// pas reconnu (une homonyme ailleurs ferait chercher dans une autre région).
    /// </summary>
    public static CachedPlace? PickPlace(JsonNode json, PlaceQuery? query = null)
    {
        foreach (var item in json["items"] as JsonArray ?? new JsonArray())
        {
            if (item is null) continue;
            var ids = JsonRead.Strings(item["criteria"]?["location"]?["placeIds"]);
            if (ids.Count == 0) continue;
            var text = JsonRead.Str(item["text"]) ?? string.Join(", ", ids);
            if (query?.Kind == PlaceKind.Department && DedupScorer.NormalizeText(text) != DedupScorer.NormalizeText(query.Text)) continue;
            var place = new CachedPlace(query?.Kind == PlaceKind.Department ? $"{text} (département)" : text, ids);
            if (query?.PostalCode is not { Length: 5 } cp) return place;
            if (text.Contains(cp, StringComparison.Ordinal) || text.Contains($"({cp[..2]})", StringComparison.Ordinal)) return place;
        }
        return null;
    }

    public async Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var ids = new List<string>();
        var complete = true;
        var total = 0;
        var maxPages = Math.Max(1, (int)Math.Ceiling(request.MaxResults / (double)PageSize));
        foreach (var chunk in request.PlaceIds.Distinct().Chunk(MaxPlacesPerQuery))
        {
            for (var page = 1; ; page++)
            {
                request.Report($"page {page}, {ids.Count} annonce(s) trouvée(s)");
                var json = await relay.SendJsonAsync(SelogerSite.Id, Label, HttpMethod.Post, "https://www.seloger.com/serp-bff/search", BuildSearch(request.Project, chunk, page).ToJsonString(), ct);
                var pageIds = (json["classifieds"] as JsonArray ?? new JsonArray())
                    .Select(c => JsonRead.Str(c?["id"])).Where(x => x is not null).Cast<string>().ToList();
                ids.AddRange(pageIds);
                var chunkTotal = JsonRead.Int(json["totalCount"]) ?? 0;
                if (pageIds.Count < PageSize || page * PageSize >= chunkTotal)
                {
                    total += chunkTotal;
                    break;
                }
                if (page >= maxPages)
                {
                    total += chunkTotal;
                    complete = false;
                    break;
                }
                await Task.Delay(800, ct);
            }
        }

        var items = new List<CollectedItem>();
        var read = 0;
        var distinct = ids.Distinct().ToList();
        foreach (var chunk in distinct.Chunk(PageSize))
        {
            request.Report($"lecture des annonces {read}/{distinct.Count}");
            read += chunk.Length;
            var json = await GetAsync($"https://www.seloger.com/classifiedList/{string.Join(',', chunk)}", ct);
            foreach (var c in json as JsonArray ?? new JsonArray())
                if (c is not null && SelogerListingParser.ParseClassified(c) is { } item) items.Add(item);
            await Task.Delay(500, ct);
        }
        return new SearchOutcome(items, complete, total);
    }

    /// <summary>
    /// Types communs -> "estateTypes" de SeLoger (relevés dans le site en novembre 2026). SeLoger n'a pas de château,
    /// loft ni maison de ville à ce niveau : ce sont des maisons ou appartements, triés ensuite par le type de l'annonce.
    /// </summary>
    public static IEnumerable<string> SiteTypes(IEnumerable<string> types) => types.Select(PropertyTypes.Normalize).SelectMany(t => t switch
    {
        PropertyTypes.Flat or PropertyTypes.Loft => ["Apartment"],
        PropertyTypes.House or PropertyTypes.TownHouse or PropertyTypes.Castle => ["House"],
        PropertyTypes.Terrain => ["Plot"],
        PropertyTypes.Building => ["Building"],
        PropertyTypes.Commercial => ["Office", "Trading"],
        PropertyTypes.Parking => ["Parking"],
        PropertyTypes.Other => ["Miscellaneous", "Agriculture_Forestry"],
        _ => Array.Empty<string>(),
    }).Distinct();

    /// <summary>Corps de serp-bff/search (mêmes champs que le site : priceMax, spaceMin, numberOfRoomsMin...).</summary>
    public static JsonObject BuildSearch(SearchProject p, IReadOnlyList<string> placeIds, int page)
    {
        var types = SiteTypes(p.PropertyTypes).ToArray();
        var criteria = new JsonObject
        {
            ["distributionTypes"] = new JsonArray(Distribution(p.Transaction)),
            ["estateTypes"] = new JsonArray(types.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["location"] = new JsonObject { ["placeIds"] = new JsonArray(placeIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) },
        };
        if (p.PriceMin is { } pmin) criteria["priceMin"] = pmin;
        if (p.PriceMax is { } pmax) criteria["priceMax"] = pmax;
        if (p.SurfaceMin is { } smin) criteria["spaceMin"] = smin;
        if (p.RoomsMin is { } rmin) criteria["numberOfRoomsMin"] = rmin;
        if (p.RoomsMax is { } rmax) criteria["numberOfRoomsMax"] = rmax;
        if (p.BedroomsMin is { } bmin) criteria["numberOfBedroomsMin"] = bmin;
        return new JsonObject
        {
            ["criteria"] = criteria,
            ["paging"] = new JsonObject { ["page"] = page, ["size"] = PageSize, ["order"] = "DateDesc" },
        };
    }

    private static string Distribution(TransactionType t) => t == TransactionType.Buy ? "Buy" : "Rent";

    private Task<JsonNode> GetAsync(string url, CancellationToken ct) =>
        relay.SendJsonAsync(SelogerSite.Id, Label, HttpMethod.Get, url, null, ct);
}
