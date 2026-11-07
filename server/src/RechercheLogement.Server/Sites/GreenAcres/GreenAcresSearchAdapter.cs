using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.GreenAcres;

/// <summary>
/// Recherche Green-Acres (www.green-acres.fr), sans compte, en lisant le HTML (relevé de novembre 2026) :
///  - annonces : GET /maison-a-vendre?cn=fr&amp;city_id=dp_65,city_33034&amp;hab_house=true&amp;…&amp;nb_min=0&amp;order=date_d&amp;p_n=N,
///    23 annonces en page 1 puis 24 ; puis la fiche de chaque annonce retenue (code postal, GPS, DPE : absents de la liste) ;
///  - lieux : département "dp_65" et code postal "pc_65240" sans appel ; commune "city_&lt;id&gt;" par l'autocomplétion
///    (POST /fr/LocationActions/SearchLocations), qui exige le jeton antiforgery et les cookies de la page d'accueil.
/// Pièges du site :
///  - la dernière recherche est gardée dans un cookie (LastPropertySearch) et FUSIONNÉE avec la suivante : aucun cookie
///    n'est conservé, le client n'a pas de conteneur de cookies (le client commun "sites", lui, en garde) ;
///  - sans "Cookie: VisitorCountry=fr", prix en dollars et surfaces en pieds carrés (selon l'IP) ;
///  - sans nb_min=0, le site élargit la recherche aux communes à 10-30 min ;
///  - une page qui n'est pas pleine est complétée par des annonces « à proximité » (ignorées par le parser).
/// Achat seulement : le relevé ne montre pas de location.
/// </summary>
public sealed partial class GreenAcresSearchAdapter(HttpClient http, int delayMs = 1200) : ISearchAdapter, IDisposable
{
    /// <summary>Le client reste à son propriétaire (le module du site, qui vit autant que le serveur).</summary>
    public void Dispose() => _gate.Dispose();

    private const string Label = "Green-Acres";
    private const string CountryCookie = "VisitorCountry=fr";
    /// <summary>Lieux par requête : ils partent tous dans le paramètre city_id.</summary>
    private const int MaxPlacesPerQuery = 40;
    /// <summary>Garde-fou : 24 annonces par page.</summary>
    private const int MaxPages = 60;
    /// <summary>Durée d'usage d'un jeton d'autocomplétion avant d'en reprendre un.</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastCallAt = long.MinValue;
    private AutocompleteSession? _session;

    /// <summary>Jeton antiforgery et cookies de la page d'accueil, pour l'autocomplétion seulement.</summary>
    public sealed record AutocompleteSession(string Token, string Cookies, DateTime At);

    /// <summary>
    /// Client sans cookies (UseCookies = false) : le client commun "sites" (IHttpClientFactory) garde les cookies dans
    /// son handler partagé, ce qui ferait fusionner les recherches. Les cookies voulus sont mis à la main, requête par requête.
    /// </summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var c = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9");
        return c;
    }

    public PlaceKinds SupportedPlaces => PlaceKinds.PostalCode | PlaceKinds.Commune | PlaceKinds.Department;

    public async Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct)
    {
        if (ResolveWithoutCall(query) is { } place) return place;
        if (query.Kind != PlaceKind.Auto) return null;
        var name = query.Text.Trim();
        if (name.Length == 0) return null;
        place = PickCommune(await SuggestAsync(name, ct), name, query.PostalCode, searchedByPostalCode: false);
        if (place is not null || query.PostalCode is not { Length: 5 } cp || !FiveDigits().IsMatch(cp)) return place;
        // Nom trop courant (la bonne commune n'est pas dans les suggestions) : les communes de son code postal.
        place = PickCommune(await SuggestAsync(cp, ct), name, cp, searchedByPostalCode: true);
        // Commune inconnue du site (commune nouvelle…) : tout son code postal ; le filtre du projet écarte les autres.
        return place ?? new CachedPlace($"{name} (tout le code postal {cp})", [$"pc_{cp}"]);
    }

    /// <summary>
    /// Lieux qui se construisent sans appel : département "dp_65" (nom ou code), code postal "pc_65240" (accepté par
    /// le site : il apparaît tel quel dans UrlConditions). Commune : null, il faut l'autocomplétion.
    /// </summary>
    public static CachedPlace? ResolveWithoutCall(PlaceQuery query)
    {
        var text = query.Text.Trim();
        switch (query.Kind)
        {
            case PlaceKind.PostalCode:
                return FiveDigits().IsMatch(text) ? PostalCodePlace(text) : null;
            case PlaceKind.Department:
                return DepartmentPlace(text);
            default:
                if (query.PostalCode is null && FiveDigits().IsMatch(text)) return PostalCodePlace(text);
                return query.PostalCode is null ? DepartmentPlace(text) : null;
        }
    }

    private static CachedPlace PostalCodePlace(string cp) => new($"Code postal {cp}", [$"pc_{cp}"]);

    private static CachedPlace? DepartmentPlace(string text)
    {
        var code = DepartmentCodeText().IsMatch(text) ? text.ToUpperInvariant() : GeoContours.DepartmentCode(text);
        return code is null ? null : new CachedPlace($"{(DepartmentCodeText().IsMatch(text) ? $"Département {code}" : text)} (département)", [$"dp_{code}"]);
    }

    /// <summary>
    /// Commune dans les suggestions de l'autocomplétion : même nom, et son code postal ; à défaut, une commune du même
    /// nom dans le même département (commune à plusieurs codes postaux). Jamais une homonyme d'un autre département.
    /// <paramref name="searchedByPostalCode"/> : la recherche portait sur le code postal, qui est donc vérifié (le site
    /// ne donne pas toujours le code postal de chaque commune). Sans code postal : seulement si le nom est sans ambiguïté.
    /// </summary>
    public static CachedPlace? PickCommune(JsonNode json, string name, string? cp, bool searchedByPostalCode)
    {
        if (json is not JsonArray entries) return null;
        var communes = entries
            .Where(e => JsonRead.Str(e?["areaType"]) == "city" && JsonRead.Str(e?["id"])?.StartsWith("city_", StringComparison.Ordinal) == true)
            .Select(e =>
            {
                var label = JsonRead.Str(e!["label"]) ?? "";
                var labelCp = LabelPostalCode().Match(label);
                return (Id: JsonRead.Str(e["id"])!, Name: labelCp.Success ? label[..labelCp.Index].Trim() : label.Trim(),
                    Cp: JsonRead.Str(e["postalCode"]) ?? (labelCp.Success ? labelCp.Groups[1].Value : null));
            })
            .Where(c => ProjectScope.SameCommune(name, c.Name) && ProjectScope.SameCommune(c.Name, name))
            .ToList();
        if (communes.Count == 0) return null;
        CachedPlace Place((string Id, string Name, string? Cp) c) => new($"{c.Name} ({c.Cp ?? cp})", [c.Id]);

        if (cp is null)
        {
            var departments = communes.Select(c => c.Cp is null ? null : string.Join(",", ProjectScope.DepartmentOf(c.Cp))).Distinct().ToList();
            return departments.Count == 1 && departments[0] is not null ? Place(communes[0]) : null;
        }
        if (communes.FirstOrDefault(c => c.Cp == cp) is { Id: not null } exact) return Place(exact);
        if (searchedByPostalCode && communes.FirstOrDefault(c => c.Cp is null) is { Id: not null } inPostalCode) return Place(inPostalCode);
        var wanted = ProjectScope.DepartmentOf(cp);
        return communes.FirstOrDefault(c => c.Cp is not null && ProjectScope.DepartmentOf(c.Cp).Intersect(wanted).Any()) is { Id: not null } sameDepartment
            ? Place(sameDepartment) : null;
    }

    public async Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        if (request.Project.Transaction != TransactionType.Buy) return new SearchOutcome([], true, 0);
        var cards = new List<GreenAcresCard>();
        var complete = true;
        var total = 0;
        var pageCount = 0;
        foreach (var chunk in request.PlaceIds.Distinct().Chunk(MaxPlacesPerQuery))
        {
            if (cards.Count >= request.MaxResults)
            {
                complete = false; // lieux suivants non lus
                break;
            }
            var read = 0;
            for (var page = 1; ; page++)
            {
                request.Report($"page {++pageCount}, {cards.Count} annonce(s) lue(s)");
                var result = GreenAcresListingParser.ParseList(await GetHtmlAsync(BuildUrl(request.Project, chunk, page), ct));
                CheckConditions(result.Conditions);
                if (page == 1) total += result.Total;
                read += result.Cards.Count;
                cards.AddRange(result.Cards.Where(c => cards.All(x => x.Id != c.Id)));
                // Fin : page vide, ou total atteint (la page 1 a 23 annonces, les suivantes 24 : pas de calcul de pages).
                if (result.Cards.Count == 0 || read >= result.Total) break;
                if (cards.Count >= request.MaxResults || page >= MaxPages)
                {
                    complete = false; // il reste des annonces plus anciennes
                    break;
                }
            }
        }
        if (cards.Count > request.MaxResults) complete = false;
        var kept = cards.Take(request.MaxResults).ToList();

        // Code postal, GPS et DPE ne sont que dans la fiche : une fiche par annonce retenue.
        var items = new List<CollectedItem>();
        var refused = false;
        foreach (var card in kept)
        {
            var data = card.Data;
            if (!refused && data.Url is { } url)
            {
                request.Report($"fiche {items.Count + 1}/{kept.Count}");
                try
                {
                    data = GreenAcresListingParser.ParseDetail(await GetHtmlAsync(url, ct), card);
                }
                catch (SiteException e)
                {
                    // Annonce retirée entre-temps (404) : la carte suffit. Refus du site : plus aucune fiche.
                    refused = e.Message.Contains("refuse", StringComparison.Ordinal);
                }
            }
            items.Add(new CollectedItem(card.Id, data));
        }
        return new SearchOutcome(items, complete, total);
    }

    /// <summary>
    /// Recherche telle que le site l'a comprise : sans euros (pays du visiteur non pris en compte) ou élargie aux
    /// alentours (nb_min différent de 0), les résultats ne correspondraient pas au projet.
    /// </summary>
    public static void CheckConditions(string? conditions)
    {
        if (conditions is null) return;
        if (Currency().Match(conditions) is { Success: true } currency && currency.Groups[1].Value != "EUR")
            throw new SiteException($"{Label} : prix en {currency.Groups[1].Value} et non en euros (cookie VisitorCountry ignoré ?).");
        if (Around().Match(conditions) is { Success: true } around && around.Groups[1].Value != "0")
            throw new SiteException($"{Label} a élargi la recherche aux communes à {around.Groups[1].Value} min (paramètre nb_min ignoré ?).");
    }

    /// <summary>Types communs -> paramètres "hab_…" de Green-Acres (relevés en novembre 2026).</summary>
    public static IEnumerable<string> SiteTypes(IEnumerable<string> types) => types.Select(PropertyTypes.Normalize).SelectMany(t => t switch
    {
        PropertyTypes.Flat or PropertyTypes.Loft => ["hab_appartement"],
        PropertyTypes.House or PropertyTypes.TownHouse => ["hab_house"],
        PropertyTypes.Castle => ["hab_castle"],
        PropertyTypes.Terrain => ["hab_land"],
        PropertyTypes.Building => ["hab_building"],
        PropertyTypes.Commercial => ["hab_business"],
        PropertyTypes.Parking => ["hab_parking"],
        PropertyTypes.Other => ["hab_other"],
        _ => Array.Empty<string>(),
    }).Distinct();

    /// <summary>
    /// Équipements communs -> paramètres du site ("pool=true"). Climatisation et accès PMR : pas de filtre, vérifiés
    /// par l'outil sur les annonces.
    /// </summary>
    public static IEnumerable<string> SiteFeatures(IEnumerable<string> features) => features.Select(f => f switch
    {
        Features.Garden => "garden",
        Features.Pool => "pool",
        Features.Terrace => "terrace",
        Features.Balcony => "balconies",
        Features.Garage => "garage",
        Features.Parking => "parking",
        Features.Cellar => "cellar",
        Features.Fireplace => "fireplace",
        Features.Elevator => "elevator",
        _ => null,
    }).OfType<string>().Distinct();

    /// <summary>URL d'une page de résultats (mêmes noms de paramètres que le formulaire du site).</summary>
    public static string BuildUrl(SearchProject p, IReadOnlyList<string> placeIds, int page)
    {
        var q = new List<(string Name, string Value)> { ("cn", "fr"), ("city_id", string.Join(",", placeIds)) };
        var types = SiteTypes(p.PropertyTypes).ToList();
        foreach (var type in types) q.Add((type, "true"));
        // Maison de ville seule : sous-type des maisons. Avec les maisons, toutes les maisons.
        var wanted = p.PropertyTypes.Select(PropertyTypes.Normalize).ToList();
        if (wanted.Contains(PropertyTypes.TownHouse) && !wanted.Contains(PropertyTypes.House)) q.Add(("hab_subtype", "town_house"));
        void Add(string name, double? value)
        {
            if (value is { } v) q.Add((name, Math.Round(v).ToString(CultureInfo.InvariantCulture)));
        }
        Add("mn_p", (double?)p.PriceMin);
        Add("mx_p", (double?)p.PriceMax);
        Add("mn_h_s", p.SurfaceMin);
        Add("mx_h_s", p.SurfaceMax);
        Add("mn_rooms", p.RoomsMin);
        Add("mx_rooms", p.RoomsMax);
        // Les filtres détaillés écartent les annonces sans l'information : envoyés seulement s'ils sont obligatoires,
        // sinon l'outil les vérifie sur les annonces (ProjectMatcher).
        if (p.IsStrict(Criteria.Bedrooms)) Add("mn_beds", p.BedroomsMin);
        if (p.IsStrict(Criteria.Bathrooms)) Add("mn_baths", p.BathroomsMin);
        if (p.IsStrict(Criteria.LandSurface))
        {
            Add("mn_l_s", p.LandSurfaceMin);
            Add("mx_l_s", p.LandSurfaceMax);
        }
        // Plusieurs classes : séparées par des virgules (un paramètre répété ne garde que la première).
        if (p.EnergyClassMax is { } energy && p.IsStrict(Criteria.EnergyClass)) q.Add(("dpe", string.Join(",", EnergyClasses.UpTo(energy))));
        if (p.IsStrict(Criteria.Features))
            foreach (var name in SiteFeatures(p.RequiredFeatures)) q.Add((name, "true"));
        q.Add(("nb_min", "0"));
        q.Add(("order", "date_d"));
        q.Add(("p_n", page.ToString(CultureInfo.InvariantCulture)));
        // Virgules gardées telles quelles (city_id=dp_65,dp_64 ; dpe=A,B,C), comme le site les écrit.
        return $"{GreenAcresListingParser.Origin}/maison-a-vendre?{string.Join("&", q.Select(x => $"{x.Name}={Uri.EscapeDataString(x.Value).Replace("%2C", ",", StringComparison.Ordinal)}"))}";
    }

    // ------------------------------------------------------------------ HTTP

    /// <summary>Page HTML, avec pour seul cookie le pays du visiteur (euros, m²).</summary>
    private async Task<string> GetHtmlAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        req.Headers.TryAddWithoutValidation("Cookie", CountryCookie);
        var (status, body, _) = await SendAsync(req, ct);
        if (status is (int)HttpStatusCode.Forbidden or (int)HttpStatusCode.TooManyRequests)
            throw new SiteException($"{Label} refuse la requête (HTTP {status}) : protection anti-robot du site.");
        if (status is < 200 or >= 300) throw new SiteException($"{Label} : HTTP {status} pour {url}");
        return body;
    }

    /// <summary>Suggestions de lieux. Jeton refusé (HTTP 400) : une nouvelle session, une seule fois.</summary>
    private async Task<JsonNode> SuggestAsync(string term, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = await SessionAsync(renew: attempt > 0, ct);
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{GreenAcresListingParser.Origin}/fr/LocationActions/SearchLocations?excludeCountries=False&term={Uri.EscapeDataString(term)}&countryId=fr");
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.TryAddWithoutValidation("RequestVerificationToken", session.Token);
            req.Headers.TryAddWithoutValidation("Cookie", session.Cookies);
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            req.Content = new ByteArrayContent([]);
            var (status, body, _) = await SendAsync(req, ct);
            if (status == (int)HttpStatusCode.BadRequest && attempt == 0) continue;
            return SiteHttp.ReadJson(status, body, Label);
        }
    }

    /// <summary>Session d'autocomplétion : page d'accueil (cookie antiforgery + jeton), gardée quelques minutes.</summary>
    private async Task<AutocompleteSession> SessionAsync(bool renew, CancellationToken ct)
    {
        if (!renew && _session is { } s && DateTime.UtcNow - s.At < SessionLifetime) return s;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{GreenAcresListingParser.Origin}/fr");
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        req.Headers.TryAddWithoutValidation("Cookie", CountryCookie);
        var (status, body, setCookies) = await SendAsync(req, ct);
        if (status is < 200 or >= 300) throw new SiteException($"{Label} : HTTP {status} sur la page d'accueil (reconnaissance des lieux).");
        return _session = ReadSession(body, setCookies, DateTime.UtcNow)
            ?? throw new SiteException($"{Label} : jeton de la recherche de lieux introuvable (le site a changé ?).");
    }

    /// <summary>
    /// Jeton (meta csrf-token ou champ __RequestVerificationToken) et cookies reçus (Set-Cookie), à renvoyer avec
    /// l'autocomplétion. Le cookie de recherche (LastPropertySearch) n'est jamais repris.
    /// </summary>
    public static AutocompleteSession? ReadSession(string html, IEnumerable<string> setCookies, DateTime at)
    {
        var token = CsrfMeta().Match(html) is { Success: true } meta ? meta.Groups[1].Value
            : CsrfInput().Match(html) is { Success: true } input ? input.Groups[1].Value : null;
        if (string.IsNullOrEmpty(token)) return null;
        var cookies = setCookies
            .Select(c => c.Split(';', 2)[0].Trim())
            .Where(c => c.Contains('=', StringComparison.Ordinal))
            .Where(c => !c.StartsWith("VisitorCountry=", StringComparison.Ordinal) && !c.StartsWith("LastPropertySearch=", StringComparison.Ordinal))
            .Prepend(CountryCookie)
            .Distinct();
        return new AutocompleteSession(WebUtility.HtmlDecode(token), string.Join("; ", cookies), at);
    }

    /// <summary>Une requête à la fois, au moins <c>delayMs</c> entre deux requêtes (pages, fiches et lieux).</summary>
    private async Task<(int Status, string Body, List<string> SetCookies)> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastCallAt == long.MinValue ? 0 : delayMs - (Environment.TickCount64 - _lastCallAt);
            if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
            try
            {
                using var res = await http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                var cookies = res.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
                return ((int)res.StatusCode, body, cookies);
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                throw new SiteException($"{Label} ne répond pas (délai dépassé).", e);
            }
            catch (HttpRequestException e)
            {
                throw new SiteException($"{Label} injoignable : {e.Message}", e);
            }
            finally
            {
                _lastCallAt = Environment.TickCount64;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    [GeneratedRegex(@"^\d{5}$")]
    private static partial Regex FiveDigits();
    [GeneratedRegex(@"^(?:\d{2}|2[AaBb]|97\d)$")]
    private static partial Regex DepartmentCodeText();
    [GeneratedRegex(@"\((\d{5})\)\s*$")]
    private static partial Regex LabelPostalCode();
    [GeneratedRegex(@"(?:^|-)currency-([A-Z]{3})(?:-|$)")]
    private static partial Regex Currency();
    [GeneratedRegex(@"(?:^|-)nb_min-(\d+)(?:-|$)")]
    private static partial Regex Around();
    [GeneratedRegex("""<meta name="csrf-token" content="([^"]+)"[^>]*>""")]
    private static partial Regex CsrfMeta();
    [GeneratedRegex("""name="__RequestVerificationToken" type="hidden" value="([^"]+)"[^>]*>""")]
    private static partial Regex CsrfInput();
}
