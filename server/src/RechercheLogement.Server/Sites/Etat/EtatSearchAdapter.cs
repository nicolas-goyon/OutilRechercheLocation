using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Etat;

/// <summary>
/// Recherche sur cessions.immobilier-etat.gouv.fr. Le site n'a pas de recherche par lieu exploitable : on lit toutes les
/// pages de la liste (une cinquantaine de biens, 10 par page), on garde les départements du projet, puis on lit les
/// fiches de ces seuls biens. Une seconde entre deux pages : le site est petit, et c'est un service public.
/// Pas de prix sur le site : la procédure de vente (enchères, appel d'offres…) le donne.
/// </summary>
public sealed class EtatSearchAdapter(IHttpClientFactory factory, GeoServices geo, int delayMs = 1000) : ISearchAdapter
{
    private const string Label = "Cessions de l'État";
    /// <summary>Garde-fou : le site compte quelques pages.</summary>
    private const int MaxPages = 30;

    /// <summary>Lieux reconnus : un département ("dep:65"), déduit d'un code postal ou d'un nom de département.</summary>
    public PlaceKinds SupportedPlaces => PlaceKinds.PostalCode | PlaceKinds.Department;

    public Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct) =>
        Task.FromResult(ResolvePlace(query));

    public static CachedPlace? ResolvePlace(PlaceQuery query)
    {
        var departments = query.Kind switch
        {
            PlaceKind.Department => GeoContours.DepartmentCode(query.Text) is { } code ? [code] : [],
            PlaceKind.PostalCode => ProjectScope.DepartmentOf(query.Text.Trim()),
            _ when query.PostalCode is { } cp => ProjectScope.DepartmentOf(cp),
            _ => [],
        };
        return departments.Count == 0 ? null : new CachedPlace($"Département {string.Join(", ", departments)}", departments.Select(d => $"dep:{d}").ToList());
    }

    public async Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        if (request.Project.Transaction != TransactionType.Buy) return new SearchOutcome([], true, 0);
        var departments = request.PlaceIds.Where(id => id.StartsWith("dep:", StringComparison.Ordinal)).Select(id => id[4..]).ToHashSet();
        var http = factory.CreateClient(SiteHttp.ClientName);

        var cards = new List<EtatCard>();
        var complete = false;
        for (var page = 0; page < MaxPages; page++)
        {
            if (page > 0) await Task.Delay(delayMs, ct);
            request.Report($"liste, page {page + 1}");
            var found = EtatListingParser.ParseList(await GetHtmlAsync(http, $"{EtatListingParser.Origin}/recherche-biens?page={page}", ct));
            var fresh = found.Where(c => cards.All(x => x.Path != c.Path)).ToList();
            if (fresh.Count == 0)
            {
                complete = true;
                break;
            }
            cards.AddRange(fresh);
        }

        var items = new List<CollectedItem>();
        var wanted = cards.Where(c => c.Department is { } d && departments.Contains(d)).Take(request.MaxResults).ToList();
        foreach (var card in wanted)
        {
            request.Report($"fiche {items.Count + 1}/{wanted.Count}");
            await Task.Delay(delayMs, ct);
            var detail = EtatListingParser.ParseDetail(await GetHtmlAsync(http, EtatListingParser.Origin + card.Path, ct), card);
            var data = detail.Data;
            if (detail.Insee is { } insee && await geo.PostalCodeOfCommuneAsync(insee, ct) is { } cp) data = data with { PostalCode = cp };
            items.Add(new CollectedItem(card.Path.Replace("/biens/", "", StringComparison.Ordinal), data));
        }
        return new SearchOutcome(items, complete, cards.Count(c => c.Department is { } d && departments.Contains(d)));
    }

    private static async Task<string> GetHtmlAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("text/html");
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException e)
        {
            throw new SiteException($"{Label} injoignable : {e.Message}", e);
        }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw new SiteException($"{Label} : HTTP {(int)res.StatusCode} pour {url}");
            return await res.Content.ReadAsStringAsync(ct);
        }
    }
}
