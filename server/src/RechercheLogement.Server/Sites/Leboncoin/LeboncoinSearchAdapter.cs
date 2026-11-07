using System.Globalization;
using System.Text.Json.Nodes;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Leboncoin;

/// <summary>
/// Recherche Leboncoin, par l'onglet du plugin (le site refuse les appels hors navigateur : DataDome). On lit la page
/// de résultats (/recherche?…), comme quand on la parcourt : son JSON __NEXT_DATA__ contient les annonces (35 par
/// page) et le nombre de pages. Paramètres relevés sur le site en novembre 2026 (chaque filtre vérifié par le nombre
/// de résultats).
/// </summary>
public sealed class LeboncoinSearchAdapter(BrowserRelay relay) : ISearchAdapter
{
    private const string Label = "Leboncoin";
    public const int PageSize = 35;
    /// <summary>Lieux par recherche : l'adresse reste courte.</summary>
    private const int MaxPlacesPerQuery = 30;

    /// <summary>Lieux reconnus sans appel au site : "d_65" (département), "65240" (code postal), "Arreau_65240" (commune).</summary>
    public PlaceKinds SupportedPlaces => PlaceKinds.PostalCode | PlaceKinds.Commune | PlaceKinds.Department;

    public Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct) =>
        Task.FromResult(ResolvePlace(query));

    public static CachedPlace? ResolvePlace(PlaceQuery query)
    {
        var text = query.Text.Trim();
        return query.Kind switch
        {
            PlaceKind.Department when GeoContours.DepartmentCode(text) is { } dep => new CachedPlace($"{text} (département)", [$"d_{dep}"]),
            PlaceKind.PostalCode when text.Length == 5 && text.All(char.IsAsciiDigit) => new CachedPlace(text, [text]),
            // Commune : nom + code postal, sans ambiguïté avec une homonyme. Sans code postal : non reconnue.
            PlaceKind.Auto when query.PostalCode is { Length: 5 } cp => new CachedPlace($"{text} ({cp})", [$"{text}_{cp}"]),
            _ => null,
        };
    }

    public async Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var items = new List<CollectedItem>();
        var complete = true;
        var total = 0;
        var maxPages = Math.Max(1, (int)Math.Ceiling(request.MaxResults / (double)PageSize));
        foreach (var chunk in request.PlaceIds.Distinct().Chunk(MaxPlacesPerQuery))
        {
            for (var page = 1; ; page++)
            {
                request.Report($"page {page}, {items.Count} annonce(s) lue(s)");
                var html = await relay.SendHtmlAsync(LeboncoinSite.Id, Label, SearchUrl(request.Project, chunk, page), ct);
                var data = LeboncoinListingParser.SearchData(html) ?? throw new SiteException($"{Label} : page de résultats illisible (le site a peut-être changé).");
                var ads = data["ads"] as JsonArray ?? new JsonArray();
                foreach (var ad in ads)
                    if (ad is not null && LeboncoinListingParser.ParseAd(ad) is { } item) items.Add(item);
                var chunkTotal = JsonRead.Int(data["total"]) ?? 0;
                var pages = JsonRead.Int(data["max_pages"]) ?? 1;
                if (ads.Count < PageSize || page >= pages)
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
            }
        }
        return new SearchOutcome(items, complete, total);
    }

    /// <summary>Types communs -> "real_estate_type" (1 maison, 2 appartement, 3 terrain, 4 parking, 5 autre).</summary>
    public static IEnumerable<string> SiteTypes(IEnumerable<string> types) => types.Select(PropertyTypes.Normalize).SelectMany(t => t switch
    {
        PropertyTypes.House or PropertyTypes.TownHouse or PropertyTypes.Castle => ["1"],
        PropertyTypes.Flat or PropertyTypes.Loft => ["2"],
        PropertyTypes.Terrain => ["3"],
        PropertyTypes.Parking => ["4"],
        PropertyTypes.Building or PropertyTypes.Commercial or PropertyTypes.Other => ["5"],
        _ => Array.Empty<string>(),
    }).Distinct().Order(StringComparer.Ordinal);

    /// <summary>
    /// Adresse de la page de résultats, la plus récente d'abord. Les critères détaillés ne sont envoyés que
    /// s'ils sont obligatoires (le site écarte les annonces sans l'information). Équipements : un seul par groupe
    /// ("outside_access" : jardin, piscine, terrasse, balcon ; "specificities" : cave, climatisation, garage ou
    /// parking), car plusieurs valeurs d'un même filtre se combinent peut-être en « ou ».
    /// </summary>
    public static string SearchUrl(SearchProject p, IReadOnlyList<string> placeIds, int page)
    {
        var q = new List<string>
        {
            $"category={(p.Transaction == TransactionType.Buy ? "9" : "10")}",
            $"locations={string.Join(',', placeIds.Select(Uri.EscapeDataString))}",
        };
        if (SiteTypes(p.PropertyTypes).ToList() is { Count: > 0 } types) q.Add($"real_estate_type={string.Join(',', types)}");
        if (Range(p.PriceMin, p.PriceMax) is { } price) q.Add($"price={price}");
        if (Range(p.SurfaceMin, p.SurfaceMax) is { } square) q.Add($"square={square}");
        if (Range(p.RoomsMin, p.RoomsMax) is { } rooms) q.Add($"rooms={rooms}");
        if (p.IsStrict(Criteria.Bedrooms) && Range(p.BedroomsMin, null) is { } bedrooms) q.Add($"bedrooms={bedrooms}");
        if (p.IsStrict(Criteria.LandSurface) && Range(p.LandSurfaceMin, p.LandSurfaceMax) is { } land) q.Add($"land_plot_surface={land}");
        if (p.IsStrict(Criteria.EnergyClass) && p.EnergyClassMax is { } energy)
            q.Add($"energy_rate={string.Join(',', EnergyClasses.UpTo(energy).Select(c => c.ToLowerInvariant()))}");
        if (p.Transaction == TransactionType.Rent && p.Furnished is { } furnished) q.Add($"furnished={(furnished ? 1 : 2)}");
        if (p.Transaction == TransactionType.Buy && SellTypes(p) is { } sell) q.Add($"immo_sell_type={sell}");
        if (p.IsStrict(Criteria.Features))
        {
            if (Single(p.RequiredFeatures, OutsideAccess) is { } outside) q.Add($"outside_access={outside}");
            if (Single(p.RequiredFeatures, Specificities) is { } specificity) q.Add($"specificities={specificity}");
        }
        q.Add("sort=time");
        q.Add("order=desc");
        if (page > 1) q.Add($"page={page}");
        return $"{LeboncoinListingParser.Origin}/recherche?{string.Join('&', q)}";
    }

    /// <summary>Ancien ("old"), neuf ("new"), viager ("viager") : rien à préciser -> null.</summary>
    public static string? SellTypes(SearchProject p)
    {
        if (p.NewBuild is null && !p.ExcludeLifeAnnuity) return null;
        var types = new List<string>();
        if (p.NewBuild != true) types.Add("old");
        if (p.NewBuild != false) types.Add("new");
        if (!p.ExcludeLifeAnnuity && p.NewBuild != true) types.Add("viager");
        return string.Join(',', types);
    }

    private static readonly Dictionary<string, string> OutsideAccess = new(StringComparer.Ordinal)
    {
        [Features.Garden] = "garden",
        [Features.Pool] = "pool",
        [Features.Terrace] = "terrace",
        [Features.Balcony] = "balcony",
    };

    private static readonly Dictionary<string, string> Specificities = new(StringComparer.Ordinal)
    {
        [Features.Cellar] = "cellar",
        [Features.AirConditioning] = "air_conditioned",
        [Features.Garage] = "with_garage_or_parking_spot",
        [Features.Parking] = "with_garage_or_parking_spot",
    };

    private static string? Single(IEnumerable<string> required, Dictionary<string, string> group) =>
        required.Where(group.ContainsKey).Select(f => group[f]).Distinct().ToList() is [var one] ? one : null;

    private static string? Range<T>(T? min, T? max) where T : struct, IFormattable =>
        min is null && max is null ? null
            : $"{(min is { } a ? a.ToString("0", CultureInfo.InvariantCulture) : "min")}-{(max is { } b ? b.ToString("0", CultureInfo.InvariantCulture) : "max")}";
}
