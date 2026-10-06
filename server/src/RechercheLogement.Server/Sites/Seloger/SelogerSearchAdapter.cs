using System.Net.Http.Json;
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
/// SeLoger a une protection anti-robot : un appel hors navigateur peut être refusé (message affiché).
/// </summary>
public sealed class SelogerSearchAdapter(IHttpClientFactory factory) : ISearchAdapter
{
    private const string Label = "SeLoger";
    private const int PageSize = 30;
    private const int MaxPlacesPerQuery = 50;

    public PlaceKinds SupportedPlaces => PlaceKinds.PostalCode | PlaceKinds.Commune | PlaceKinds.Department | PlaceKinds.Region;

    public async Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct)
    {
        var http = factory.CreateClient(SiteHttp.ClientName);
        var url = $"https://www.seloger.com/search-mfe-bff/autocomplete/suggestion?query={Uri.EscapeDataString(query.Text.Trim())}&limit=5&distributionType={Distribution(transaction)}";
        var json = await SiteHttp.SendJsonAsync(http, Get(url), Label, ct);
        return PickPlace(json, query);
    }

    /// <summary>
    /// Premier lieu proposé. Département : le libellé doit être le nom du département. Commune choisie dans les
    /// suggestions : on préfère le lieu dont le libellé contient son code postal ou son département ("Rodez (12)").
    /// </summary>
    public static CachedPlace? PickPlace(JsonNode json, PlaceQuery? query = null)
    {
        CachedPlace? first = null;
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
            first ??= place;
        }
        return first;
    }

    public async Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var http = factory.CreateClient(SiteHttp.ClientName);
        var ids = new List<string>();
        var complete = true;
        var total = 0;
        var maxPages = Math.Max(1, (int)Math.Ceiling(request.MaxResults / (double)PageSize));
        foreach (var chunk in request.PlaceIds.Distinct().Chunk(MaxPlacesPerQuery))
        {
            for (var page = 1; ; page++)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "https://www.seloger.com/serp-bff/search")
                {
                    Content = JsonContent.Create(BuildSearch(request.Project, chunk, page)),
                };
                AddBrowserHeaders(req);
                var json = await SiteHttp.SendJsonAsync(http, req, Label, ct);
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
        foreach (var chunk in ids.Distinct().Chunk(PageSize))
        {
            var json = await SiteHttp.SendJsonAsync(http, Get($"https://www.seloger.com/classifiedList/{string.Join(',', chunk)}"), Label, ct);
            foreach (var c in json as JsonArray ?? new JsonArray())
                if (c is not null && SelogerListingParser.ParseClassified(c) is { } item) items.Add(item);
            await Task.Delay(500, ct);
        }
        return new SearchOutcome(items, complete, total);
    }

    /// <summary>Corps de serp-bff/search (mêmes champs que le site : priceMax, spaceMin, numberOfRoomsMin...).</summary>
    public static JsonObject BuildSearch(SearchProject p, IReadOnlyList<string> placeIds, int page)
    {
        var types = p.PropertyTypes.Select(t => t switch { "house" => "House", _ => "Apartment" }).Distinct().ToArray();
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

    private static HttpRequestMessage Get(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(req);
        return req;
    }

    private static void AddBrowserHeaders(HttpRequestMessage req)
    {
        req.Headers.Referrer = new Uri("https://www.seloger.com/classified-search");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.seloger.com");
    }
}
