using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;

namespace RechercheLogement.Server.Sites.Paruvendu;

/// <summary>
/// ParuVendu immobilier (www.paruvendu.fr) : achat et location. Recherche seulement (<see cref="ParuvenduSearchAdapter"/>,
/// pages HTML lues par <see cref="ParuvenduListingParser"/>), pas de parser pour le plugin.
/// </summary>
public sealed class ParuvenduSite(IHttpClientFactory http, GeoServices geo) : ISiteModule
{
    public const string Id = "paruvendu";

    public SiteInfo Info { get; } = new(Id, "ParuVendu", SiteCategory.ImmoPortal,
        "Pages de résultats du site (HTML), triées par date de mise à jour. Code postal déduit de la commune de l'annonce.");
    public ISearchAdapter Search { get; } = new ParuvenduSearchAdapter(http, geo);

    IListingParser? ISiteModule.Parser => null;
    ISearchAdapter? ISiteModule.Search => Search;
}
