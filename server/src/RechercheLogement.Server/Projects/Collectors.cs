using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;

namespace RechercheLogement.Server.Projects;

/// <summary>Lieu reconnu par un site : libellé et identifiants internes du site.</summary>
public sealed record LocationMatch(string Label, List<string> Ids);

/// <summary>Une annonce renvoyée par un site, déjà convertie au format commun.</summary>
public sealed record CollectedItem(string SiteId, ListingData Data);

/// <summary>Erreur lisible par l'utilisateur (affichée sur la page du projet).</summary>
public sealed class CollectorException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Interroge UN site d'annonces pour un projet. Ajouter un site : une classe qui implémente cette
/// interface, enregistrée dans Program.cs, et une entrée disponible dans <see cref="SourceSites"/>.
/// </summary>
public interface ISiteCollector
{
    string SiteId { get; }
    Task<LocationMatch?> ResolveLocationAsync(string query, TransactionType transaction, CancellationToken ct);
    Task<IReadOnlyList<CollectedItem>> SearchAsync(SearchProject project, IReadOnlyList<string> locationIds, CancellationToken ct);
}

public static class CollectorHttp
{
    public const string ClientName = "collectors";

    /// <summary>Client HTTP des collecteurs : en-têtes d'un navigateur, décompression, délai de 30 s.</summary>
    public static IServiceCollection AddCollectors(this IServiceCollection services)
    {
        services.AddHttpClient(ClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
                c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9");
                c.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
        services.AddSingleton<ISiteCollector, BieniciCollector>();
        services.AddSingleton<ISiteCollector, SelogerCollector>();
        return services;
    }

    /// <summary>Envoie la requête. Transforme les refus et pannes en message clair.</summary>
    public static async Task<JsonNode> SendJsonAsync(HttpClient http, HttpRequestMessage request, string siteLabel, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(request, ct);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new CollectorException($"{siteLabel} ne répond pas (délai dépassé).", e);
        }
        catch (HttpRequestException e)
        {
            throw new CollectorException($"{siteLabel} injoignable : {e.Message}", e);
        }
        using (res)
        {
            if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new CollectorException($"{siteLabel} refuse la requête (HTTP {(int)res.StatusCode}) : protection anti-robot du site.");
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new CollectorException($"{siteLabel} : HTTP {(int)res.StatusCode} {Truncate(text, 160)}");
            try
            {
                return JsonNode.Parse(text) ?? throw new CollectorException($"{siteLabel} : réponse vide.");
            }
            catch (JsonException e)
            {
                // Page HTML (captcha) au lieu du JSON attendu.
                throw new CollectorException($"{siteLabel} : réponse inattendue (page de vérification anti-robot ?).", e);
            }
        }
    }

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    internal static decimal? Dec(JsonNode? n) => n is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : FirstOf(n) is JsonValue f && f.TryGetValue<decimal>(out var d2) ? d2 : null;
    internal static double? Dbl(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : FirstOf(n) is JsonValue f && f.TryGetValue<double>(out var d2) ? d2 : null;
    internal static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : FirstOf(n) is JsonValue f && f.TryGetValue<int>(out var i2) ? i2 : null;
    internal static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    internal static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>Bien'ici renvoie parfois une plage ([min, max]) au lieu d'une valeur : on garde la première.</summary>
    private static JsonNode? FirstOf(JsonNode? n) => n is JsonArray a && a.Count > 0 ? a[0] : null;

    internal static string? Excerpt(string? text) =>
        text is null ? null : Truncate(DedupScorer.NormalizeText(Regex.Replace(text, "<[^>]+>", " ")), 600);
}

// =====================================================================
// Bien'ici : API publique de la liste de résultats (realEstateAds.json)
// =====================================================================

/// <summary>
/// Lieux : https://res.bienici.com/suggest.json?q=… (zoneIds).
/// Annonces : https://www.bienici.com/realEstateAds.json?filters={…} (100 par page, plus récentes d'abord).
/// </summary>
public sealed class BieniciCollector(IHttpClientFactory factory) : ISiteCollector
{
    private const int PageSize = 100;
    private const int MaxPages = 3;

    public string SiteId => "bienici";

    public async Task<LocationMatch?> ResolveLocationAsync(string query, TransactionType transaction, CancellationToken ct)
    {
        var http = factory.CreateClient(CollectorHttp.ClientName);
        var url = $"https://res.bienici.com/suggest.json?q={Uri.EscapeDataString(query.Trim())}";
        var json = await CollectorHttp.SendJsonAsync(http, new HttpRequestMessage(HttpMethod.Get, url), "Bien'ici", ct);
        return PickPlace(json, query);
    }

    /// <summary>Premier lieu avec des zoneIds. Un code postal saisi doit figurer dans les codes postaux du lieu.</summary>
    internal static LocationMatch? PickPlace(JsonNode json, string query)
    {
        if (json is not JsonArray places) return null;
        var cp = Regex.IsMatch(query.Trim(), @"^\d{5}$") ? query.Trim() : null;
        foreach (var place in places)
        {
            var zones = (place?["zoneIds"] as JsonArray)?.Select(z => z?.ToString()).Where(z => !string.IsNullOrEmpty(z)).Cast<string>().ToList();
            if (zones is not { Count: > 0 }) continue;
            var postalCodes = (place!["postalCodes"] as JsonArray)?.Select(z => z?.ToString()).ToList() ?? [];
            if (cp is not null && !postalCodes.Contains(cp)) continue;
            var name = CollectorHttp.Str(place["name"]) ?? query;
            var label = postalCodes.Count == 1 ? $"{name} ({postalCodes[0]})" : name;
            return new LocationMatch(label, zones);
        }
        return null;
    }

    public async Task<IReadOnlyList<CollectedItem>> SearchAsync(SearchProject project, IReadOnlyList<string> locationIds, CancellationToken ct)
    {
        var http = factory.CreateClient(CollectorHttp.ClientName);
        var items = new List<CollectedItem>();
        for (var page = 0; page < MaxPages; page++)
        {
            var filters = BuildFilters(project, locationIds, page);
            var url = $"https://www.bienici.com/realEstateAds.json?filters={Uri.EscapeDataString(filters.ToJsonString())}";
            var json = await CollectorHttp.SendJsonAsync(http, new HttpRequestMessage(HttpMethod.Get, url), "Bien'ici", ct);
            var ads = json["realEstateAds"] as JsonArray ?? new JsonArray();
            foreach (var ad in ads)
                if (ad is not null && MapAd(ad) is { } item) items.Add(item);
            var total = CollectorHttp.Int(json["total"]) ?? 0;
            if (ads.Count < PageSize || (page + 1) * PageSize >= total) break;
            await Task.Delay(800, ct); // une requête à la fois, sans insister
        }
        return items;
    }

    /// <summary>Filtres de l'API (mêmes noms que le site : minPrice, maxArea, minRooms...).</summary>
    internal static JsonObject BuildFilters(SearchProject p, IReadOnlyList<string> zoneIds, int page)
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

    /// <summary>Annonce de realEstateAds.json -> format commun (mêmes règles que le plugin, parseBieniciApiAd).</summary>
    internal static CollectedItem? MapAd(JsonNode ad)
    {
        var id = CollectorHttp.Str(ad["id"]);
        if (id is null) return null;
        var pos = ad["blurInfo"]?["position"];
        var lat = CollectorHttp.Dbl(pos?["lat"]);
        var lon = CollectorHttp.Dbl(pos?["lon"]);
        var photos = (ad["photos"] as JsonArray)?
            .Select(p => CollectorHttp.Str(p?["url"]) ?? CollectorHttp.Str(p?["url_photo"]))
            .Where(u => u is not null).Cast<string>().Take(4).ToList();
        var rooms = CollectorHttp.Int(ad["roomsQuantity"]);
        var surface = CollectorHttp.Dbl(ad["surfaceArea"]);
        var type = CollectorHttp.Str(ad["propertyType"]);
        var data = new ListingData
        {
            Url = $"https://www.bienici.com/annonce/{id}",
            Title = CollectorHttp.Str(ad["title"]) ?? DefaultTitle(type, rooms, surface),
            Transaction = CollectorHttp.Str(ad["adType"]) switch { "rent" => TransactionType.Rent, "buy" => TransactionType.Buy, _ => null },
            PropertyType = type,
            Price = CollectorHttp.Dec(ad["price"]),
            Charges = CollectorHttp.Dec(ad["charges"]),
            Surface = surface,
            Rooms = rooms,
            Bedrooms = CollectorHttp.Int(ad["bedroomsQuantity"]),
            Floor = CollectorHttp.Int(ad["floor"]),
            Furnished = CollectorHttp.Bool(ad["isFurnished"]),
            PostalCode = CollectorHttp.Str(ad["postalCode"]),
            City = CollectorHttp.Str(ad["city"]),
            District = CollectorHttp.Str(ad["district"]?["libelle"]) ?? CollectorHttp.Str(ad["district"]?["name"]),
            Geo = lat is not null && lon is not null ? new GeoPoint(lat.Value, lon.Value, CollectorHttp.Dbl(ad["blurInfo"]?["radius"])) : null,
            AgencyRef = CollectorHttp.Str(ad["reference"]),
            AgencyName = CollectorHttp.Str(ad["accountDisplayName"]),
            Photos = photos is { Count: > 0 } ? photos : null,
            DescriptionExcerpt = CollectorHttp.Excerpt(CollectorHttp.Str(ad["description"])),
            PublishedAt = CollectorHttp.Str(ad["publicationDate"]),
        };
        return new CollectedItem(id, data);
    }

    private static string DefaultTitle(string? type, int? rooms, double? surface)
    {
        var label = type switch { "house" => "Maison", "flat" => "Appartement", "loft" => "Loft", _ => "Bien" };
        if (rooms is not null) label += $" {rooms} pièce{(rooms > 1 ? "s" : "")}";
        if (surface is not null) label += $" {surface.Value.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR"))} m²";
        return label;
    }
}

// =====================================================================
// SeLoger : API de la liste de résultats (serp-bff) + détail par lots (classifiedList)
// =====================================================================

/// <summary>
/// Lieux : https://www.seloger.com/search-mfe-bff/autocomplete/suggestion?query=… (placeIds).
/// Annonces : POST https://www.seloger.com/serp-bff/search (identifiants, plus récentes d'abord), puis
/// GET https://www.seloger.com/classifiedList/&lt;ids&gt; (données des annonces).
/// SeLoger utilise une protection anti-robot : un appel hors navigateur peut être refusé (message affiché).
/// </summary>
public sealed class SelogerCollector(IHttpClientFactory factory) : ISiteCollector
{
    private const int PageSize = 30;
    private const int MaxPages = 4;

    public string SiteId => "seloger";

    public async Task<LocationMatch?> ResolveLocationAsync(string query, TransactionType transaction, CancellationToken ct)
    {
        var http = factory.CreateClient(CollectorHttp.ClientName);
        var url = $"https://www.seloger.com/search-mfe-bff/autocomplete/suggestion?query={Uri.EscapeDataString(query.Trim())}&limit=5&distributionType={Distribution(transaction)}";
        var json = await CollectorHttp.SendJsonAsync(http, Get(url), "SeLoger", ct);
        return PickPlace(json);
    }

    internal static LocationMatch? PickPlace(JsonNode json)
    {
        foreach (var item in json["items"] as JsonArray ?? new JsonArray())
        {
            var ids = (item?["criteria"]?["location"]?["placeIds"] as JsonArray)?
                .Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList();
            if (ids is { Count: > 0 }) return new LocationMatch(CollectorHttp.Str(item!["text"]) ?? string.Join(", ", ids), ids);
        }
        return null;
    }

    public async Task<IReadOnlyList<CollectedItem>> SearchAsync(SearchProject project, IReadOnlyList<string> locationIds, CancellationToken ct)
    {
        var http = factory.CreateClient(CollectorHttp.ClientName);
        var ids = new List<string>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://www.seloger.com/serp-bff/search")
            {
                Content = JsonContent.Create(BuildSearch(project, locationIds, page)),
            };
            AddBrowserHeaders(req);
            var json = await CollectorHttp.SendJsonAsync(http, req, "SeLoger", ct);
            var pageIds = (json["classifieds"] as JsonArray ?? new JsonArray())
                .Select(c => CollectorHttp.Str(c?["id"])).Where(x => x is not null).Cast<string>().ToList();
            ids.AddRange(pageIds);
            var total = CollectorHttp.Int(json["totalCount"]) ?? 0;
            if (pageIds.Count < PageSize || page * PageSize >= total) break;
            await Task.Delay(800, ct);
        }

        var items = new List<CollectedItem>();
        foreach (var chunk in ids.Distinct().Chunk(PageSize))
        {
            var json = await CollectorHttp.SendJsonAsync(http, Get($"https://www.seloger.com/classifiedList/{string.Join(',', chunk)}"), "SeLoger", ct);
            foreach (var c in json as JsonArray ?? new JsonArray())
                if (c is not null && MapClassified(c) is { } item) items.Add(item);
            await Task.Delay(500, ct);
        }
        return items;
    }

    /// <summary>Corps de serp-bff/search (mêmes champs que le site : priceMax, spaceMin, numberOfRoomsMin...).</summary>
    internal static JsonObject BuildSearch(SearchProject p, IReadOnlyList<string> placeIds, int page)
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

    /// <summary>Annonce de classifiedList -> format commun (mêmes règles que le plugin, parseSelogerSerpClassified).</summary>
    internal static CollectedItem? MapClassified(JsonNode c)
    {
        var id = CollectorHttp.Str(c["id"]);
        if (id is null) return null;
        var raw = c["rawData"];
        var addr = c["location"]?["address"];
        var facts = (c["hardFacts"]?["keyfacts"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? [];
        var rooms = CollectorHttp.Int(raw?["nbroom"]);
        var surface = CollectorHttp.Dbl(raw?["surface"]?["main"]);
        var heading = CollectorHttp.Str(c["hardFacts"]?["title"]);
        var photos = (c["gallery"]?["images"] as JsonArray)?
            .Select(i => CollectorHttp.Str(i?["url"])).Where(u => u is not null).Cast<string>().Take(4).ToList();
        var furnished = facts.Any(f => Regex.IsMatch(f, @"^\s*meubl", RegexOptions.IgnoreCase)) ? true
            : facts.Any(f => Regex.IsMatch(f, @"non\s+meubl", RegexOptions.IgnoreCase)) ? false : (bool?)null;
        var data = new ListingData
        {
            Url = CollectorHttp.Str(c["url"]) ?? $"https://www.seloger.com/annonces/{id}",
            Title = Title(heading, rooms, surface),
            Transaction = CollectorHttp.Str(raw?["distributionType"]) switch
            {
                { } d when d.StartsWith("RENT", StringComparison.OrdinalIgnoreCase) => TransactionType.Rent,
                { } d when d.StartsWith("BUY", StringComparison.OrdinalIgnoreCase) || d.StartsWith("SALE", StringComparison.OrdinalIgnoreCase) => TransactionType.Buy,
                _ => null,
            },
            PropertyType = CollectorHttp.Str(raw?["propertyType"]) switch
            {
                "APARTMENT" or "FLAT" or "STUDIO" => "flat",
                "HOUSE" or "VILLA" => "house",
                "LOFT" => "loft",
                _ => null,
            },
            Price = CollectorHttp.Dec(raw?["price"]),
            Surface = surface,
            Rooms = rooms,
            Bedrooms = CollectorHttp.Int(raw?["nbbedroom"]),
            Floor = Floor(facts),
            Furnished = furnished,
            PostalCode = CollectorHttp.Str(addr?["zipCode"]),
            City = CollectorHttp.Str(addr?["city"]),
            District = CollectorHttp.Str(addr?["district"]),
            AgencyRef = CollectorHttp.Str(raw?["offererMarketingKey"]),
            AgencyName = CollectorHttp.Str(c["provider"]?["intermediaryCard"]?["title"])?.Trim(),
            Photos = photos is { Count: > 0 } ? photos : null,
            DescriptionExcerpt = CollectorHttp.Excerpt(CollectorHttp.Str(c["mainDescription"]?["description"])),
            PublishedAt = CollectorHttp.Str(c["metadata"]?["creationDate"]),
        };
        return new CollectedItem(id, data);
    }

    private static string? Title(string? heading, int? rooms, double? surface)
    {
        if (heading is null) return null;
        var t = Regex.Replace(heading, @"\s+à\s+(louer|vendre)\b.*$", "", RegexOptions.IgnoreCase).Trim();
        if (rooms is not null) t += $" {rooms} pièce{(rooms > 1 ? "s" : "")}";
        if (surface is not null) t += $" {surface.Value.ToString("0.##", CultureInfo.GetCultureInfo("fr-FR"))} m²";
        return t;
    }

    /// <summary>"7ème étage" -> 7, "Étage 1/1" -> 1, "Rez-de-chaussée" -> 0.</summary>
    internal static int? Floor(IEnumerable<string> facts)
    {
        foreach (var f in facts)
        {
            var t = DedupScorer.NormalizeText(f);
            if (t.Contains("rez de chaussee", StringComparison.Ordinal)) return 0;
            var m = Regex.Match(t, @"(\d+)\s*(?:e|er|eme|ere)?\s*etage");
            if (!m.Success) m = Regex.Match(t, @"etage\s*(\d+)");
            if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return null;
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
