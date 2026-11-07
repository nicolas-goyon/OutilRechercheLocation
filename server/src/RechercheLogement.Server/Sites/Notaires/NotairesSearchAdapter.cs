using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Notaires;

/// <summary>
/// Recherche sur www.immobilier.notaires.fr, par son API JSON publique (pub-services, sans compte) :
///  - lieux : département direct ("dep:65") ; commune et code postal par l'autocomplétion du site, qui donne les
///    identifiants Hexavia de ses localités ("loc:28605") ;
///  - annonces : /inotr-www-annonces/v1/annonces?page=N&amp;parPage=100, dernières modifiées d'abord (pas de tri par
///    date de publication) ; puis la fiche de chaque annonce (DPE, équipements, photos, description complète) et,
///    une fois par office, son nom et son SIRET.
/// Le robots.txt du site demande 10 s entre deux requêtes : <paramref name="delayMs"/> est respecté entre toutes les
/// requêtes de l'adaptateur (lieux compris), une à la fois. Les annonces sont peu nombreuses (une cinquantaine de
/// maisons par département).
/// </summary>
public sealed partial class NotairesSearchAdapter(IHttpClientFactory factory, int delayMs = NotairesSearchAdapter.DefaultDelayMs) : ISearchAdapter, IDisposable
{
    public const int DefaultDelayMs = 10_000;
    private const string Label = "Notaires de France";
    private const string Api = NotairesListingParser.Origin + "/pub-services";
    /// <summary>Maximum accepté par l'API (au-delà, elle renvoie quand même 100).</summary>
    private const int PageSize = 100;
    /// <summary>Localités par requête : la liste part dans l'URL.</summary>
    private const int MaxLocalitiesPerQuery = 150;
    /// <summary>Fiches illisibles d'affilée avant d'arrêter de les lire (site qui refuse) : la liste suffit alors.</summary>
    private const int MaxDetailFailures = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    /// <summary>Offices déjà lus (numéro CRPCEN -> nom, SIREN) : ils changent rarement.</summary>
    private readonly ConcurrentDictionary<string, NotairesOffice> _offices = new(StringComparer.Ordinal);

    public void Dispose() => _gate.Dispose();

    public PlaceKinds SupportedPlaces => PlaceKinds.PostalCode | PlaceKinds.Commune | PlaceKinds.Department;

    public async Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct)
    {
        var text = query.Text.Trim();
        if (query.Kind == PlaceKind.Department) return Department(text);
        if (query.Kind == PlaceKind.PostalCode || PostalCodeText().IsMatch(text))
            return PickPostalCode(await SuggestAsync(text, ct), text);
        if (query.PostalCode is { Length: 5 } cp)
        {
            // Les localités du code postal, puis celles qui portent le nom de la commune : jamais une homonyme
            // d'un autre département. Nom introuvable (commune nouvelle, nom écrit autrement) : tout le code postal,
            // les autres communes sont écartées par le filtre du projet.
            var json = await SuggestAsync(cp, ct);
            return PickCommune(json, text, cp)
                ?? (PickPostalCode(json, cp) is { } zone ? new CachedPlace($"{text} (tout le code postal {cp})", zone.Ids) : null);
        }
        if (Department(text) is { } department) return department;
        return PickFirst(await SuggestAsync(text, ct, size: 10));
    }

    private Task<JsonNode> SuggestAsync(string text, CancellationToken ct, int size = 99) =>
        GetAsync($"{Api}/geo-consultation/v1/localites/suggestion?suggestion={Uri.EscapeDataString(text)}&size={size}", ct);

    /// <summary>Département par son nom ("Hautes-Pyrénées") ou son code ("65", "2A").</summary>
    public static CachedPlace? Department(string text)
    {
        var code = DepartmentCodeText().IsMatch(text) ? text.ToUpperInvariant() : GeoContours.DepartmentCode(text);
        return code is null ? null : new CachedPlace($"{text} (département)", [$"dep:{code}"]);
    }

    /// <summary>Localités de l'autocomplétion dont le libellé finit par "(code postal)".</summary>
    public static CachedPlace? PickPostalCode(JsonNode json, string cp)
    {
        var ids = Localities(json).Where(l => l.PostalCode == cp).Select(l => $"loc:{l.Id}").Distinct().ToList();
        return ids.Count == 0 ? null : new CachedPlace($"Code postal {cp}", ids);
    }

    /// <summary>Localités du code postal <paramref name="cp"/> qui portent le nom de la commune.</summary>
    public static CachedPlace? PickCommune(JsonNode json, string commune, string cp)
    {
        var inPostalCode = Localities(json).Where(l => l.PostalCode == cp).ToList();
        // Même nom d'abord ; sinon un nom qui commence par celui de la commune ("St Lary" -> "ST LARY SOULAN").
        var same = inPostalCode.Where(l => SameName(commune, l.Name)).ToList();
        if (same.Count == 0) same = inPostalCode.Where(l => ProjectScope.SameCommune(commune, l.Name)).ToList();
        return same.Count == 0 ? null : new CachedPlace($"{commune} ({cp})", same.Select(l => $"loc:{l.Id}").Distinct().ToList());

        bool SameName(string a, string b) => Words(a) == Words(b);
        static string Words(string s) => string.Join(' ', Core.Dedup.DedupScorer.NormalizeText(s).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w switch { "st" => "saint", "ste" => "sainte", _ => w }));
    }

    /// <summary>Texte libre sans code postal : première localité proposée.</summary>
    public static CachedPlace? PickFirst(JsonNode json) =>
        Localities(json).FirstOrDefault() is { } l ? new CachedPlace($"{l.Name} ({l.PostalCode})", [$"loc:{l.Id}"]) : null;

    private sealed record Locality(string Id, string Name, string? PostalCode);

    /// <summary>Entrées "LOCALITE" : {"valeur": id Hexavia, "label": "BAGNERES DE BIGORRE (65200)"}.</summary>
    private static IEnumerable<Locality> Localities(JsonNode json)
    {
        foreach (var entry in json as JsonArray ?? [])
        {
            if (entry is null || JsonRead.Str(entry["type"]) is { } type && type != "LOCALITE") continue;
            var id = JsonRead.Str(entry["valeur"]);
            var label = JsonRead.Str(entry["label"]);
            if (id is null || label is null) continue;
            var m = LocalityLabel().Match(label);
            yield return m.Success ? new Locality(id, m.Groups[1].Value.Trim(), m.Groups[2].Value) : new Locality(id, label, null);
        }
    }

    public async Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var p = request.Project;
        var departments = request.PlaceIds.Where(id => id.StartsWith("dep:", StringComparison.Ordinal)).Select(id => id[4..]).Distinct().ToList();
        var localities = request.PlaceIds.Where(id => id.StartsWith("loc:", StringComparison.Ordinal)).Select(id => id[4..]).Distinct().ToList();
        // Départements et localités dans des requêtes séparées (le site ne dit pas s'il les combine en "et" ou en "ou").
        var places = new List<string>();
        if (departments.Count > 0) places.Add($"departements={string.Join(',', departments)}");
        places.AddRange(localities.Chunk(MaxLocalitiesPerQuery).Select(c => $"localites={string.Join(',', c)}"));

        var cards = new List<NotairesCard>();
        var complete = true;
        var total = 0;
        foreach (var place in places)
        {
            for (var page = 1; ; page++)
            {
                if (cards.Count >= request.MaxResults)
                {
                    complete = false; // il reste des annonces moins récentes
                    break;
                }
                request.Report($"page {page}, {cards.Count} annonce(s) lue(s)");
                var (found, placeTotal, pages) = NotairesListingParser.ParseList(await GetAsync($"{Api}/inotr-www-annonces/v1/annonces?{BuildQuery(p, place, page)}", ct));
                cards.AddRange(found.Where(c => cards.All(x => x.Id != c.Id)));
                if (page == 1) total += placeTotal;
                if (page >= pages || found.Count == 0) break;
            }
        }

        // Vente Immo-Interactif terminée (encore listée par le site des mois après) : plus à vendre.
        var wanted = cards.Where(c => !c.AuctionClosed).Take(request.MaxResults).ToList();
        var items = new List<CollectedItem>();
        var failures = 0;
        foreach (var card in wanted)
        {
            var data = card.Data;
            if (failures < MaxDetailFailures)
            {
                request.Report($"fiche {items.Count + 1}/{wanted.Count}");
                try
                {
                    data = NotairesListingParser.ParseDetail(await GetAsync($"{Api}/inotr-www-annonces/v1/annonces/{card.Id}", ct), data);
                    failures = 0;
                }
                catch (SiteException)
                {
                    failures++; // annonce retirée entre-temps (HTTP 400) : celle de la liste suffit
                }
                if (await OfficeAsync(card, ct) is { } office) data = data with { AgencyName = office.Name ?? data.AgencyName, AgencySiren = office.Siren ?? data.AgencySiren };
            }
            items.Add(new CollectedItem(card.Id, data));
        }
        return new SearchOutcome(items, complete, total);
    }

    /// <summary>Office notarial de l'annonce, lu une seule fois par office. Illisible : null.</summary>
    private async Task<NotairesOffice?> OfficeAsync(NotairesCard card, CancellationToken ct)
    {
        if (card.Crpcen is not { } crpcen) return null;
        if (_offices.TryGetValue(crpcen, out var known)) return known;
        try
        {
            var office = NotairesListingParser.ParseOffice(await GetAsync($"{Api}/inotr-www-annonces/v1/annonces/{card.Id}/office", ct));
            _offices[crpcen] = office;
            return office;
        }
        catch (SiteException)
        {
            return null;
        }
    }

    /// <summary>
    /// Une requête à la fois, au moins <c>delayMs</c> après la précédente (robots.txt : Crawl-delay 10).
    /// </summary>
    private async Task<JsonNode> GetAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastCall + TimeSpan.FromMilliseconds(delayMs) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri($"{NotairesListingParser.Origin}/fr/annonces-immobilieres-liste");
                return await SiteHttp.SendJsonAsync(factory.CreateClient(SiteHttp.ClientName), req, Label, ct);
            }
            finally
            {
                _lastCall = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Types communs -> codes "typeBiens" du site (relevés en novembre 2026).</summary>
    public static IEnumerable<string> SiteTypes(IEnumerable<string> types) => types.Select(PropertyTypes.Normalize).SelectMany(t => t switch
    {
        PropertyTypes.Flat or PropertyTypes.Loft => ["APP"],
        PropertyTypes.House or PropertyTypes.TownHouse or PropertyTypes.Castle => ["MAI"],
        PropertyTypes.Terrain => ["TER"],
        PropertyTypes.Building => ["IMM"],
        PropertyTypes.Commercial => ["LAC", "COM"],
        PropertyTypes.Parking => ["GAR"],
        PropertyTypes.Other => ["DIV", "AGR", "VIG"],
        _ => Array.Empty<string>(),
    }).Distinct();

    /// <summary>
    /// Paramètres de la liste (noms du site). Chaque liste tient dans un seul paramètre, valeurs séparées par des
    /// virgules (un paramètre répété : HTTP 400). Filtres que le site n'a pas (terrain, DPE, année, équipements,
    /// travaux) : vérifiés par l'outil sur les fiches. Pièces non envoyées : "nbPieces" manque ou vaut 0 sur 4
    /// annonces sur 10, que le filtre écarterait.
    /// </summary>
    public static string BuildQuery(SearchProject p, string place, int page)
    {
        var q = new List<string>
        {
            $"page={page}",
            $"parPage={PageSize}",
            place,
            // Achat : vente classique, Immo-Interactif et enchères, comme le site.
            $"typeTransactions={(p.Transaction == TransactionType.Buy ? "VENTE,VNI,VAE" : "LOCATION")}",
            "tri=DATE_MODIFICATION_DESC",
        };
        var types = SiteTypes(p.PropertyTypes).ToList();
        if (types.Count > 0) q.Add($"typeBiens={string.Join(',', types)}");
        string N(decimal v) => v.ToString("0", CultureInfo.InvariantCulture);
        string D(double v) => v.ToString("0", CultureInfo.InvariantCulture);
        if (p.PriceMin is { } pmin) q.Add($"prixMin={N(pmin)}");
        if (p.PriceMax is { } pmax) q.Add($"prixMax={N(pmax)}");
        if (p.SurfaceMin is { } smin) q.Add($"surfaceMin={D(smin)}");
        if (p.SurfaceMax is { } smax) q.Add($"surfaceMax={D(smax)}");
        // "nbChambres" manque sur 4 annonces sur 10 : le filtre du site les écarte, envoyé seulement si obligatoire.
        if (p.BedroomsMin is { } bmin && p.IsStrict(Criteria.Bedrooms)) q.Add($"nbChambresMin={bmin}");
        // "neuf=OUI" filtre ; "neuf=NON" et "viager=NON" sont ignorés par le site (vérifié en novembre 2026) : l'ancien
        // et l'exclusion des viagers sont vérifiés par l'outil (toutes les annonces disent ancien/neuf et viager).
        if (p.NewBuild == true) q.Add("neuf=OUI");
        return string.Join('&', q);
    }

    [GeneratedRegex(@"^\d{5}$")]
    private static partial Regex PostalCodeText();

    [GeneratedRegex(@"^(\d{2,3}|2[AaBb])$")]
    private static partial Regex DepartmentCodeText();

    [GeneratedRegex(@"^(.*)\((\d{5})\)\s*$")]
    private static partial Regex LocalityLabel();
}
