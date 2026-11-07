using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Paruvendu;

/// <summary>
/// Recherche ParuVendu immobilier, sans compte (relevé en novembre 2026) :
///  - lieux : autocomplétion JSON du site (autocompleteLocalisation) -> département, code postal ou commune (INSEE) ;
///  - annonces : pages HTML de listeAnnonces, 30 par page, triées par date de mise à jour (le site n'a pas de date
///    de publication), HTTP 410 après la dernière page.
/// Identifiants de lieu : "dep:65", "cp:65400", "com:65025:65400" (INSEE et code postal de la commune).
///
/// La liste ne donne pas le code postal, seulement "Ville (65)". Il est déduit, sans lire les fiches : commune de
/// l'annonce dans les communes de son département (geo.api.gouv.fr, une requête par département, gardée en mémoire),
/// en gardant le code postal demandé au site quand la commune en a plusieurs. Le centre de la commune sert de position
/// (le site ne donne pas de GPS). Seules les annonces retenues dont le code postal reste inconnu (commune absente de
/// geo.api, ou à plusieurs codes postaux dans une recherche par département) font lire leur fiche, 10 au plus.
/// </summary>
public sealed partial class ParuvenduSearchAdapter(IHttpClientFactory factory, GeoServices geo, int delayMs = 1000) : ISearchAdapter
{
    private const string Label = "ParuVendu";
    /// <summary>Annonces par page (hors encarts sponsorisés) ; pas de paramètre de taille de page.</summary>
    private const int PageSize = 30;
    /// <summary>Lieux par requête : les valeurs sont séparées par des virgules dans l'URL.</summary>
    private const int MaxPlacesPerQuery = 30;
    /// <summary>Garde-fou : pages lues au plus par requête.</summary>
    private const int MaxPages = 60;
    /// <summary>Fiches lues au plus par recherche, pour un code postal introuvable autrement.</summary>
    private const int MaxDetails = 10;
    /// <summary>Précision de la position (centre de la commune), comme <see cref="AreaFilter"/>.</summary>
    private const double CommunePrecisionM = 2000;

    public PlaceKinds SupportedPlaces => PlaceKinds.PostalCode | PlaceKinds.Commune | PlaceKinds.Department;

    public async Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct)
    {
        var place = PickPlace(await SuggestAsync(query.Text, ct), query);
        if (place is not null || query is not { Kind: PlaceKind.Auto, PostalCode: { Length: 5 } cp }) return place;
        // Nom courant (Saint-Michel...) : la bonne commune n'est pas toujours dans les suggestions du nom. Celles du
        // code postal listent les communes de ce code.
        await Task.Delay(delayMs / 2, ct);
        return PickPlace(await SuggestAsync(cp, ct), query);
    }

    private async Task<JsonNode> SuggestAsync(string text, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"{ParuvenduListingParser.Origin}/communfo/defaultcommunfo/defaultcommunfo/autocompleteLocalisation?avecCodePostal=1&term={Uri.EscapeDataString(SuggestTerm(text))}");
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await SiteHttp.SendJsonAsync(factory.CreateClient(SiteHttp.ClientName), req, Label, ct);
    }

    /// <summary>
    /// Texte envoyé à l'autocomplétion, sans accents : le site ne trouve rien pour "Hautes-Pyrénées" ou
    /// "Argelès-Gazost" (vérifié en novembre 2026), mais trouve "Hautes-Pyrenees". Tirets et casse gardés.
    /// </summary>
    public static string SuggestTerm(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var kept = decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
        return new string(kept).Normalize(NormalizationForm.FormC).Replace("œ", "oe", StringComparison.Ordinal).Replace("Œ", "Oe", StringComparison.Ordinal);
    }

    /// <summary>
    /// Choix dans les suggestions du site : code postal -> ce code ; département -> le département de ce nom ;
    /// commune choisie (avec son code postal) -> la commune de ce nom qui a ce code postal, sinon de ce nom dans le
    /// même département, jamais une homonyme d'un autre département ; texte libre -> commune puis département de ce nom.
    /// </summary>
    public static CachedPlace? PickPlace(JsonNode json, PlaceQuery query)
    {
        if (json is not JsonArray items) return null;
        var text = query.Text.Trim();
        var suggestions = items.OfType<JsonObject>().ToList();
        if (query.Kind == PlaceKind.PostalCode || (query is { Kind: PlaceKind.Auto, PostalCode: null } && PostalCodeText().IsMatch(text)))
            return suggestions.Any(s => JsonRead.Str(s["cp"]) == text) ? new CachedPlace(text, [$"cp:{text}"]) : null;

        CachedPlace? Department() => suggestions
            .Where(s => JsonRead.Str(s["category"]) == "Departements" && JsonRead.Str(s["cp"]) is not null && SameName(DepartmentName(s), text))
            .Select(s => new CachedPlace($"{DepartmentName(s)} (département)", [$"dep:{JsonRead.Str(s["cp"])!.ToUpperInvariant()}"]))
            .FirstOrDefault();
        if (query.Kind == PlaceKind.Department) return Department();

        var communes = suggestions.Where(s => JsonRead.Str(s["category"]) == "Communes" && JsonRead.Str(s["codeInsee"]) is not null
            && JsonRead.Str(s["lib"]) is { } lib && SameName(lib, text)).ToList();
        if (query.PostalCode is not { Length: 5 } wanted)
            return communes.Select(s => Commune(s, JsonRead.Str(s["cp"]))).FirstOrDefault(p => p is not null) ?? Department();
        var exact = communes.FirstOrDefault(s => PostalCodes(s).Contains(wanted));
        if (exact is not null) return Commune(exact, wanted);
        var sameDepartment = communes.FirstOrDefault(s => PostalCodes(s).Any(c => ProjectScope.DepartmentOf(c).Intersect(ProjectScope.DepartmentOf(wanted)).Any()));
        return sameDepartment is null ? null : Commune(sameDepartment, PostalCodes(sameDepartment).First());
    }

    private static CachedPlace? Commune(JsonObject s, string? cp) =>
        cp is { Length: 5 } && JsonRead.Str(s["codeInsee"]) is { } insee
            ? new CachedPlace($"{JsonRead.Str(s["lib"])} ({cp})", [$"com:{insee}:{cp}"])
            : null;

    private static List<string> PostalCodes(JsonObject s)
    {
        var list = JsonRead.Strings(s["liste_cp"]);
        if (JsonRead.Str(s["cp"]) is { } cp && !list.Contains(cp)) list.Add(cp);
        return list;
    }

    /// <summary>"Hautes-Pyrénées (65)" -> "Hautes-Pyrénées".</summary>
    private static string DepartmentName(JsonObject s) => TrailingCode().Replace(JsonRead.Str(s["value"]) ?? "", "").Trim();

    /// <summary>Même nom de commune (accents, tirets, "St" ignorés), dans les deux sens : "Argelès" n'est pas "Argelès-Gazost".</summary>
    private static bool SameName(string a, string b) => ProjectScope.SameCommune(a, b) && ProjectScope.SameCommune(b, a);

    // ------------------------------------------------------------------ recherche

    /// <summary>Une requête au site : un paramètre de lieu, et les codes postaux demandés (pour déduire celui des annonces).</summary>
    public sealed record PlaceGroup(string Parameter, IReadOnlyList<string> PostalCodes);

    /// <summary>
    /// Lieux -> requêtes : départements ensemble ("lo=65,64"), codes postaux ensemble ("lo=65400,65200"), communes
    /// ensemble ("codeINSEE=65025:0,65440:0,"). La commune exige le rayon ":0", sinon le site élargit à 15 km.
    /// </summary>
    public static List<PlaceGroup> Groups(IEnumerable<string> placeIds)
    {
        var ids = placeIds.Distinct().ToList();
        var departments = ids.Where(i => i.StartsWith("dep:", StringComparison.Ordinal)).Select(i => i[4..]).ToList();
        var postalCodes = ids.Where(i => i.StartsWith("cp:", StringComparison.Ordinal)).Select(i => i[3..]).ToList();
        var communes = ids.Where(i => i.StartsWith("com:", StringComparison.Ordinal)).Select(i => i[4..].Split(':')).Where(p => p.Length == 2).ToList();
        var groups = new List<PlaceGroup>();
        groups.AddRange(departments.Chunk(MaxPlacesPerQuery).Select(c => new PlaceGroup($"lo={string.Join(',', c)}", [])));
        groups.AddRange(postalCodes.Chunk(MaxPlacesPerQuery).Select(c => new PlaceGroup($"lo={string.Join(',', c)}", c)));
        groups.AddRange(communes.Chunk(MaxPlacesPerQuery).Select(c => new PlaceGroup(
            $"codeINSEE={string.Concat(c.Select(x => $"{x[0]}:0,"))}", c.Select(x => x[1]).Distinct().ToList())));
        return groups;
    }

    public async Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var p = request.Project;
        if (p.PropertyTypes.Count > 0 && !SiteTypes(p.PropertyTypes).Any()) return new SearchOutcome([], true, 0); // locaux : absents du site
        var http = factory.CreateClient(SiteHttp.ClientName);
        var found = new List<(ParuvenduCard Card, PlaceGroup Group)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;
        var total = 0;
        var requests = 0;
        foreach (var group in Groups(request.PlaceIds))
        {
            if (found.Count >= request.MaxResults)
            {
                complete = false;
                break;
            }
            var read = 0;
            for (var page = 1; page <= MaxPages; page++)
            {
                if (requests++ > 0) await Task.Delay(delayMs, ct);
                request.Report($"page {page}, {found.Count} annonce(s) lue(s)");
                var html = await GetHtmlAsync(http, SearchUrl(p, group.Parameter, page), ct);
                if (html is null) break; // HTTP 410 : après la dernière page
                var parsed = ParuvenduListingParser.ParseList(html, p.Transaction);
                if (page == 1) total += parsed.Total ?? 0;
                read += parsed.Cards.Count;
                foreach (var card in parsed.Cards.Where(c => !seen.Contains(c.Id)))
                {
                    if (found.Count >= request.MaxResults)
                    {
                        complete = false; // annonces de cette page laissées de côté
                        continue;
                    }
                    seen.Add(card.Id);
                    found.Add((card, group));
                }
                // Dernière page : moins de 30 annonces, ou toutes celles annoncées lues (évite la requête du 410).
                if (parsed.Cards.Count < PageSize || (parsed.Total is { } t && read >= t) || (page == 1 && parsed.Total is null && parsed.Cards.Count == 0)) break;
                if (found.Count >= request.MaxResults)
                {
                    complete = false; // il reste des annonces plus anciennes
                    break;
                }
                if (page == MaxPages) complete = false;
            }
        }

        var items = new List<CollectedItem>();
        var details = 0;
        var communes = new Dictionary<string, List<PlanCommune>?>(StringComparer.Ordinal);
        foreach (var (card, group) in found)
        {
            var data = Deduced(p, card.Data);
            var commune = await CommuneAsync(card, communes, ct);
            if (DeducePostalCode(commune, group.PostalCodes) is { } cp) data = data with { PostalCode = cp };
            if (commune is not null) data = data with { Geo = new GeoPoint(commune.Center.Lat, commune.Center.Lon, CommunePrecisionM) };
            if (data.PostalCode is null && details < MaxDetails && data.City is { } city && ProjectMatcher.Matches(p, data, out _))
            {
                details++;
                request.Report($"fiche {details} (code postal)");
                await Task.Delay(delayMs, ct);
                try
                {
                    if (await GetHtmlAsync(http, data.Url!, ct) is { } html && ParuvenduListingParser.PostalCodeOfDetail(html, city, card.Department) is { } fromDetail)
                        data = data with { PostalCode = fromDetail };
                }
                catch (SiteException)
                {
                    // Annonce gardée sans code postal : la recherche du site est déjà limitée à la zone.
                }
            }
            items.Add(new CollectedItem(card.Id, data));
        }
        return new SearchOutcome(items, complete, Math.Max(total, items.Count));
    }

    /// <summary>
    /// Ce que la recherche garantit, que la liste ne dit pas : neuf ou ancien (filtre "natureProjet"), équipements,
    /// travaux et salles de bain demandés au site (critères obligatoires). Sans cela, un critère obligatoire écarterait
    /// toutes les annonces ("non renseigné"). Salles de bain : le minimum demandé (le site dit "au moins").
    /// </summary>
    public static ListingData Deduced(SearchProject p, ListingData d)
    {
        var natures = Natures(p);
        bool? newBuild = natures.Count == 0 ? null : natures.Contains("neuf") ? (natures.Contains("ancien") ? null : true) : false;
        var features = p.IsStrict(Criteria.Features) ? SiteFeatures(p.RequiredFeatures).Select(f => (f.Feature, (bool?)true)).ToArray() : [];
        return d with
        {
            NewBuild = d.NewBuild ?? newBuild,
            WorkNeeded = d.WorkNeeded ?? (p.WorkNeeded == true && p.IsStrict(Criteria.WorkNeeded) ? true : null),
            Bathrooms = d.Bathrooms ?? (p.IsStrict(Criteria.Bathrooms) ? p.BathroomsMin : null),
            Features = features.Length == 0 ? d.Features : Features.Of([.. (d.Features ?? new Dictionary<string, bool>()).Select(kv => (kv.Key, (bool?)kv.Value)), .. features]),
        };
    }

    /// <summary>
    /// Commune de l'annonce (nom + département), d'après geo.api.gouv.fr. Service indisponible (après un second
    /// essai) : null, sans le redemander pour chaque annonce du même département (<paramref name="known"/>).
    /// </summary>
    private async Task<PlanCommune?> CommuneAsync(ParuvenduCard card, Dictionary<string, List<PlanCommune>?> known, CancellationToken ct)
    {
        if (card.Department is not { } department || card.Data.City is not { } city) return null;
        if (!known.TryGetValue(department, out var communes))
        {
            for (var attempt = 1; attempt <= 2 && communes is null; attempt++)
            {
                try
                {
                    communes = await geo.CommunesAsync(department, ct);
                }
                catch (SiteException) when (attempt == 1)
                {
                    await Task.Delay(delayMs, ct);
                }
                catch (SiteException)
                {
                    // Annonces gardées sans code postal déduit (fiche lue pour quelques-unes).
                }
            }
            known[department] = communes;
        }
        return communes is null ? null : CommuneOf(communes, city);
    }

    /// <summary>Commune de ce nom (exactement, accents et "St" ignorés) ; plusieurs homonymes dans le département : null.</summary>
    public static PlanCommune? CommuneOf(IEnumerable<PlanCommune> communes, string city)
    {
        var matches = communes.Where(c => SameName(c.Name, city)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Code postal d'une annonce : celui de sa commune s'il est seul, ou le seul de ses codes que la recherche demandait.
    /// Commune inconnue : le code demandé s'il n'y en avait qu'un (recherche par code postal ou par commune).
    /// </summary>
    public static string? DeducePostalCode(PlanCommune? commune, IReadOnlyList<string> asked)
    {
        if (commune is null) return asked.Count == 1 ? asked[0] : null;
        var inAsked = commune.PostalCodes.Where(asked.Contains).Distinct().ToList();
        if (inAsked.Count == 1) return inAsked[0];
        return commune.PostalCodes.Distinct().Count() == 1 ? commune.PostalCodes[0] : null;
    }

    /// <summary>Page HTML ; null pour HTTP 410 (page après la dernière, ou annonce retirée).</summary>
    private static async Task<string?> GetHtmlAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("text/html");
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new SiteException($"{Label} ne répond pas (délai dépassé).", e);
        }
        catch (HttpRequestException e)
        {
            throw new SiteException($"{Label} injoignable : {e.Message}", e);
        }
        using (res)
        {
            var status = (int)res.StatusCode;
            if (res.StatusCode == HttpStatusCode.Gone) return null;
            if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new SiteException($"{Label} refuse la requête (HTTP {status}) : protection anti-robot du site.");
            if (!res.IsSuccessStatusCode) throw new SiteException($"{Label} : HTTP {status} pour {url}");
            return await res.Content.ReadAsStringAsync(ct);
        }
    }

    /// <summary>
    /// URL de la liste (noms des champs du formulaire de recherche du site). Le site redirige un département ou un
    /// code postal vers son URL "SEO" avec les mêmes paramètres : la redirection est suivie.
    /// </summary>
    public static string SearchUrl(SearchProject p, string place, int page)
    {
        var q = new List<string> { $"tt={(p.Transaction == TransactionType.Buy ? 1 : 5)}" };
        q.AddRange((p.PropertyTypes.Count == 0 ? AllTypes : SiteTypes(p.PropertyTypes)).Select(t => $"{t}=1"));
        q.Add("pa=FR");
        q.Add(place);
        void Add(string name, object? value)
        {
            if (value is not null) q.Add($"{name}={Convert.ToString(value, CultureInfo.InvariantCulture)}");
        }
        Add("px0", p.PriceMin is { } pmin ? decimal.Floor(pmin) : null);
        Add("px1", p.PriceMax is { } pmax ? decimal.Ceiling(pmax) : null);
        Add("sur0", p.SurfaceMin is { } smin ? (int)Math.Floor(smin) : null);
        Add("sur1", p.SurfaceMax is { } smax ? (int)Math.Ceiling(smax) : null);
        // Pièces en dizaines : 10 = 1 pièce … 50 = 5 pièces, 55 = plus de 5.
        Add("nbp0", p.RoomsMin is { } rmin ? (rmin >= 6 ? 55 : Math.Max(rmin, 1) * 10) : null);
        Add("nbp1", p.RoomsMax is { } rmax && rmax <= 5 ? Math.Max(rmax, 1) * 10 : null);
        // Le site écarte les annonces sans l'information : seulement pour un critère obligatoire (Criteria).
        if (p.IsStrict(Criteria.Bedrooms)) Add("enrNbChb", p.BedroomsMin is { } b and > 0 ? Math.Min(b, 5) : null);
        if (p.IsStrict(Criteria.Bathrooms)) Add("enrNbSdB", p.BathroomsMin is { } s and > 0 ? Math.Min(s, 5) : null);
        if (p.IsStrict(Criteria.LandSurface))
        {
            Add("surT0", p.LandSurfaceMin is { } lmin ? (int)Math.Floor(lmin) : null);
            Add("surT1", p.LandSurfaceMax is { } lmax ? (int)Math.Ceiling(lmax) : null);
        }
        if (p.IsStrict(Criteria.Features))
            foreach (var (_, name) in SiteFeatures(p.RequiredFeatures)) q.Add($"{name}=1");
        // "travaux=1" ne garde que les annonces qui le disent : "sans travaux" est vérifié par l'outil.
        if (p.WorkNeeded == true && p.IsStrict(Criteria.WorkNeeded)) q.Add("travaux=1");
        if (p.Transaction == TransactionType.Rent && p.Furnished == true) q.Add("mb=1");
        foreach (var nature in Natures(p)) q.Add($"natureProjet%5B%5D={nature}");
        q.Add("ddlTri=dateMiseAJour");
        q.Add("ddlOrd=desc");
        q.Add($"p={page}");
        return $"{ParuvenduListingParser.Origin}/immobilier/annonceimmofo/liste/listeAnnonces?{string.Join('&', q)}";
    }

    /// <summary>
    /// Filtre "natureProjet" (achat) : neuf, ancien, viager. Toutes les annonces ont l'information : envoyé même si le
    /// critère n'est pas obligatoire. Viagers écartés par le projet : "ancien" et "neuf" seulement.
    /// </summary>
    public static List<string> Natures(SearchProject p)
    {
        if (p.Transaction != TransactionType.Buy) return [];
        return p.NewBuild switch
        {
            true => ["neuf"],
            false => p.ExcludeLifeAnnuity ? ["ancien"] : ["ancien", "viager"],
            null => p.ExcludeLifeAnnuity ? ["ancien", "neuf"] : [],
        };
    }

    private static readonly string[] AllTypes =
        ["tbMai", "tbVil", "tbCha", "tbPro", "tbHot", "tbMou", "tbFer", "tbApp", "tbDup", "tbChb", "tbLof", "tbAtl", "tbPla", "tbPen", "tbRem", "tbImm", "tbTer", "tbPar", "tbAut"];

    /// <summary>
    /// Types communs -> cases à cocher du site (panneau des filtres, novembre 2026). Maison : tout le groupe maisons
    /// (le site range à part villas, chalets, moulins, fermes). Pas de locaux commerciaux sur le site.
    /// </summary>
    public static IEnumerable<string> SiteTypes(IEnumerable<string> types) => types.Select(PropertyTypes.Normalize).SelectMany(t => t switch
    {
        PropertyTypes.House => ["tbMai", "tbVil", "tbCha", "tbMou", "tbFer"],
        PropertyTypes.TownHouse => ["tbMai"],
        PropertyTypes.Castle => ["tbPro", "tbHot"],
        PropertyTypes.Flat => ["tbApp", "tbDup"],
        PropertyTypes.Loft => ["tbLof", "tbAtl", "tbPla"],
        PropertyTypes.Terrain => ["tbTer"],
        PropertyTypes.Building => ["tbImm"],
        PropertyTypes.Parking => ["tbPar"],
        PropertyTypes.Other => ["tbRem", "tbPen", "tbChb", "tbAut"],
        _ => Array.Empty<string>(),
    }).Distinct();

    /// <summary>
    /// Équipements communs -> filtres du site. Garage : "box / garage fermé". Pas de filtre cheminée, climatisation
    /// ni accès PMR : vérifiés par l'outil.
    /// </summary>
    public static IEnumerable<(string Feature, string Name)> SiteFeatures(IEnumerable<string> features) => features.Select(f => (Feature: f, Name: f switch
    {
        Features.Garden => "enrJar",
        Features.Pool => "enrPis",
        Features.Terrace => "enrTer",
        Features.Balcony => "enrBal",
        Features.Garage => "enrBox",
        Features.Parking => "enrPk",
        Features.Cellar => "enrCave",
        Features.Elevator => "enrAsc",
        _ => null,
    })).Where(x => x.Name is not null).Select(x => (x.Feature, x.Name!)).Distinct();

    [GeneratedRegex(@"^\d{5}$")]
    private static partial Regex PostalCodeText();
    [GeneratedRegex(@"\s*\(\w{2,3}\)\s*$")]
    private static partial Regex TrailingCode();
}
