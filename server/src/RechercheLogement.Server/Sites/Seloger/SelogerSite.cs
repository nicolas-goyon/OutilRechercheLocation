using RechercheLogement.Core.Projects;

namespace RechercheLogement.Server.Sites.Seloger;

/// <summary>
/// SeLoger (www.seloger.com). Fonctionnalités :
///  - <see cref="SelogerListingParser"/> : annonce de classifiedList -> format commun.
///  - <see cref="SelogerSearchAdapter"/> : lieux (autocomplete) + recherche (serp-bff/search, classifiedList).
/// Côté plugin (navigateur) : plugin/src/sites/seloger/.
/// </summary>
public sealed class SelogerSite(IHttpClientFactory http) : ISiteModule
{
    public const string Id = "seloger";

    public SiteInfo Info { get; } = new(Id, "SeLoger", SiteCategory.ImmoPortal, "API de recherche du site. Peut être bloquée par sa protection anti-robot.");
    public IListingParser Parser { get; } = new SelogerListingParser();
    public ISearchAdapter Search { get; } = new SelogerSearchAdapter(http);

    IListingParser? ISiteModule.Parser => Parser;
    ISearchAdapter? ISiteModule.Search => Search;
}
