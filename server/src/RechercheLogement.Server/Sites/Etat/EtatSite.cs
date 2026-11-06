using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;

namespace RechercheLogement.Server.Sites.Etat;

/// <summary>
/// Cessions immobilières de l'État (cessions.immobilier-etat.gouv.fr) : biens vendus par l'État, aux enchères ou sur
/// appel d'offres. Recherche seulement (<see cref="EtatSearchAdapter"/>), pas de parser pour le plugin.
/// </summary>
public sealed class EtatSite(IHttpClientFactory http, GeoServices geo) : ISiteModule
{
    public const string Id = "etat";

    public SiteInfo Info { get; } = new(Id, "Cessions de l'État", SiteCategory.PublicSales,
        "Biens vendus par l'État (achat seulement). Pas de prix affiché : un prix maximum ne les écarte pas.");
    public ISearchAdapter Search { get; } = new EtatSearchAdapter(http, geo);

    IListingParser? ISiteModule.Parser => null;
    ISearchAdapter? ISiteModule.Search => Search;
}
