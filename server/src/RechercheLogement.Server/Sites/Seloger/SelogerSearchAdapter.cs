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
    /// Commune au code postal connu : cherchée par son code postal. Les suggestions SeLoger ne donnent que le nom
    /// ("Espelette"), sans département : impossible d'écarter une homonyme ("Saint-Pierre" renvoie La Réunion). Le
    /// code postal est sans ambiguïté ; les annonces des autres communes de ce code postal sont écartées ensuite
    /// (<c>ProjectScope.OutsidePlaces</c>).
    /// </summary>
    public PlaceQuery Prepare(PlaceQuery query) =>
        query is { Kind: PlaceKind.Auto, PostalCode: { Length: 5 } cp } ? new PlaceQuery(PlaceKind.PostalCode, cp) : query;

    /// <summary>
    /// Lieu proposé qui correspond exactement à la demande. Code postal : libellé = le code postal. Département et
    /// commune : libellé = le nom (sans tenir compte des accents et de la casse). Commune avec code postal : un
    /// libellé qui contient son code postal ou son département ("Rodez (12)") est aussi accepté. Rien d'autre : une
    /// suggestion approchante ("Saint-Pierre-d'Oléron" pour "Saint-Pierre") ferait chercher ailleurs.
    /// </summary>
    public static CachedPlace? PickPlace(JsonNode json, PlaceQuery? query = null)
    {
        foreach (var item in json["items"] as JsonArray ?? new JsonArray())
        {
            if (item is null) continue;
            var ids = JsonRead.Strings(item["criteria"]?["location"]?["placeIds"]);
            if (ids.Count == 0) continue;
            var text = JsonRead.Str(item["text"]) ?? string.Join(", ", ids);
            if (query is null) return new CachedPlace(text, ids);
            var wanted = query.Text.Trim();
            var sameName = DedupScorer.NormalizeText(text) == DedupScorer.NormalizeText(wanted);
            switch (query.Kind)
            {
                case PlaceKind.PostalCode when text.Trim() == wanted:
                    return new CachedPlace(text, ids);
                case PlaceKind.Department when sameName:
                    return new CachedPlace($"{text} (département)", ids);
                case PlaceKind.Auto when query.PostalCode is { Length: 5 } cp
                    && (text.Contains(cp, StringComparison.Ordinal) || text.Contains($"({cp[..2]})", StringComparison.Ordinal)):
                    return new CachedPlace(text, ids);
                case PlaceKind.Auto when query.PostalCode is null && sameName:
                    return new CachedPlace(text, ids);
            }
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
        if (p.LandSurfaceMin is { } lmin) criteria["plotSpaceMin"] = lmin;
        if (p.LandSurfaceMax is { } lmax) criteria["plotSpaceMax"] = lmax;
        if (p.YearBuiltMin is { } ymin) criteria["yearOfConstructionMin"] = ymin;
        if (p.YearBuiltMax is { } ymax) criteria["yearOfConstructionMax"] = ymax;
        if (p.EnergyClassMax is { } energy) criteria["energyCertificateClass"] = Strings(EnergyClasses.UpTo(energy));
        if (SiteFeatures(p.RequiredFeatures).ToList() is { Count: > 0 } features) criteria["featuresIncluded"] = Strings(features);
        if (ProjectTypes(p) is { } projectTypes) criteria["projectTypes"] = Strings(projectTypes);
        return new JsonObject
        {
            ["criteria"] = criteria,
            ["paging"] = new JsonObject { ["page"] = page, ["size"] = PageSize, ["order"] = "DateDesc" },
        };
    }

    /// <summary>
    /// Équipements communs -> "featuresIncluded" (valeurs relevées dans le site, novembre 2026). Terrasse et balcon :
    /// un seul filtre ; garage et parking aussi. Cheminée et climatisation : vérifiées par l'outil sur les annonces.
    /// </summary>
    public static IEnumerable<string> SiteFeatures(IEnumerable<string> features) => features.Select(f => f switch
    {
        Features.Garden => "Garden",
        Features.Pool => "Swimming_Pool",
        Features.Terrace or Features.Balcony => "Balcony_Terrace",
        Features.Garage or Features.Parking => "Parking_Garage",
        Features.Cellar => "Cellar",
        Features.Elevator => "Elevator",
        Features.Accessible => "Reduce_Mobility_Access",
        _ => null,
    }).OfType<string>().Distinct();

    /// <summary>
    /// Achat : ancien ("Resale"), neuf ("New_Build", "Projected") et viager ("Life_Annuity"). null : tous (rien à
    /// préciser). Location : jamais.
    /// </summary>
    public static IReadOnlyList<string>? ProjectTypes(SearchProject p)
    {
        if (p.Transaction != TransactionType.Buy || (p.NewBuild is null && !p.ExcludeLifeAnnuity)) return null;
        var types = new List<string>();
        if (p.NewBuild != true) types.Add("Resale");
        if (p.NewBuild != false) types.AddRange(["New_Build", "Projected"]);
        if (!p.ExcludeLifeAnnuity && p.NewBuild != true) types.Add("Life_Annuity");
        return types;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static string Distribution(TransactionType t) => t == TransactionType.Buy ? "Buy" : "Rent";

    private Task<JsonNode> GetAsync(string url, CancellationToken ct) =>
        relay.SendJsonAsync(SelogerSite.Id, Label, HttpMethod.Get, url, null, ct);
}
