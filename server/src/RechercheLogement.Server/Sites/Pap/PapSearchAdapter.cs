using System.Globalization;
using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Pap;

/// <summary>
/// Recherche PAP (de particulier à particulier), par l'onglet du plugin : le site refuse les appels hors navigateur.
/// Adresse de recherche relevée en novembre 2026 : /annonce/vente-maisons-g429g4644-a-partir-de-100-m2… (lieux
/// "g&lt;id&gt;" enchaînés, critères dans l'adresse, un type de bien par recherche : le site ne les combine pas).
/// Page suivante : lien « Suivante » ; les pages « à proximité » (/proximite/…) sont ignorées. La fiche de chaque
/// annonce retenue est lue (code postal, terrain, DPE, date, description).
/// </summary>
public sealed class PapSearchAdapter(BrowserRelay relay) : ISearchAdapter
{
    private const string Label = "PAP";
    private const int MaxPlacesPerQuery = 10;

    public PlaceKinds SupportedPlaces => PlaceKinds.PostalCode | PlaceKinds.Commune | PlaceKinds.Department;

    public async Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct)
    {
        var text = query.Text.Trim();
        var json = await relay.SendJsonAsync(PapSite.Id, Label, HttpMethod.Get, $"{PapListingParser.Origin}/json/ac-geo?q={Uri.EscapeDataString(text)}", null, ct);
        return PickPlace(PapListingParser.ParsePlaces(json), query);
    }

    /// <summary>
    /// Suggestion qui correspond exactement : code postal ("65240"), département ("Hautes-Pyrénées - 65"), commune
    /// ("Arreau (65240)", même nom et même code postal : jamais une homonyme).
    /// </summary>
    public static CachedPlace? PickPlace(IReadOnlyList<(long Id, string Name)> places, PlaceQuery query)
    {
        var text = query.Text.Trim();
        var dep = query.Kind == PlaceKind.Department ? GeoContours.DepartmentCode(text) : null;
        foreach (var (id, name) in places)
        {
            var ok = query.Kind switch
            {
                PlaceKind.PostalCode => name == text,
                PlaceKind.Department => dep is not null && name.EndsWith($" - {dep}", StringComparison.Ordinal),
                _ when query.PostalCode is { } cp => DedupScorer.NormalizeText(name) == DedupScorer.NormalizeText($"{text} ({cp})"),
                _ => false,
            };
            if (ok) return new CachedPlace(name, [$"g{id}"]);
        }
        return null;
    }

    public async Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var p = request.Project;
        var cards = new List<PapCard>();
        var complete = true;
        foreach (var slug in SiteTypes(p.PropertyTypes))
        {
            foreach (var chunk in request.PlaceIds.Distinct().Chunk(MaxPlacesPerQuery))
            {
                string? url = SearchUrl(p, slug, chunk);
                var page = 1;
                while (url is not null)
                {
                    request.Report($"{slug}, page {page}");
                    var html = await relay.SendHtmlAsync(PapSite.Id, Label, url.StartsWith('/') ? PapListingParser.Origin + url : url, ct);
                    cards.AddRange(PapListingParser.ParseList(html).Where(c => cards.All(x => x.Id != c.Id)));
                    url = PapListingParser.NextPage(html);
                    if (url is not null && cards.Count >= request.MaxResults)
                    {
                        complete = false;
                        break;
                    }
                    page++;
                }
            }
        }

        var items = new List<CollectedItem>();
        var wanted = cards.Take(request.MaxResults).ToList();
        foreach (var card in wanted)
        {
            request.Report($"fiche {items.Count + 1}/{wanted.Count}");
            var html = await relay.SendHtmlAsync(PapSite.Id, Label, PapListingParser.Origin + card.Path, ct);
            items.Add(new CollectedItem(card.Id, PapListingParser.ParseDetail(html, card.Data with
            {
                Transaction = p.Transaction,
            })));
        }
        return new SearchOutcome(items, complete && cards.Count <= request.MaxResults, cards.Count);
    }

    /// <summary>Types communs -> mot de l'adresse PAP (un par recherche).</summary>
    public static IEnumerable<string> SiteTypes(IEnumerable<string> types) => types.Select(PropertyTypes.Normalize).Select(t => t switch
    {
        PropertyTypes.House or PropertyTypes.TownHouse or PropertyTypes.Castle => "maisons",
        PropertyTypes.Flat or PropertyTypes.Loft => "appartements",
        PropertyTypes.Terrain => "terrains",
        PropertyTypes.Parking => "garages-parkings",
        PropertyTypes.Building => "immeubles",
        _ => null,
    }).OfType<string>().Distinct();

    /// <summary>
    /// Adresse de la première page : "/annonce/vente-maisons-g429g4644-a-partir-de-3-chambres-jusqu-a-300000-euros-
    /// a-partir-de-100-m2". Chambres : seulement si obligatoire (le site écarte les annonces sans l'information).
    /// </summary>
    public static string SearchUrl(SearchProject p, string typeSlug, IReadOnlyList<string> placeIds)
    {
        var url = $"{PapListingParser.Origin}/annonce/{(p.Transaction == TransactionType.Buy ? "vente" : "locations")}-{typeSlug}-{string.Concat(placeIds)}";
        if (p.IsStrict(Criteria.Bedrooms) && p.BedroomsMin is { } bedrooms) url += $"-a-partir-de-{bedrooms}-chambres";
        if (Range(p.PriceMin, p.PriceMax, "euros") is { } price) url += price;
        if (Range(p.SurfaceMin, p.SurfaceMax, "m2") is { } surface) url += surface;
        return url;
    }

    private static string? Range<T>(T? min, T? max, string unit) where T : struct, IFormattable
    {
        static string F(T v) => v.ToString("0", CultureInfo.InvariantCulture);
        return (min, max) switch
        {
            ({ } a, { } b) => $"-entre-{F(a)}-et-{F(b)}-{unit}",
            ({ } a, null) => $"-a-partir-de-{F(a)}-{unit}",
            (null, { } b) => $"-jusqu-a-{F(b)}-{unit}",
            _ => null,
        };
    }
}
