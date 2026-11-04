using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Seloger;

/// <summary>
/// SeLoger (www.seloger.com). Fonctionnalités :
///  - <see cref="SelogerListingParser"/> : annonce de classifiedList -> format commun.
///  - <see cref="SelogerSearchAdapter"/> : lieux (autocomplete) + recherche (serp-bff/search, classifiedList).
/// Côté plugin (navigateur) : plugin/src/sites/seloger/.
/// </summary>
public sealed class SelogerSite(BrowserRelay relay) : ISiteModule
{
    public const string Id = "seloger";

    public SiteInfo Info { get; } = new(Id, "SeLoger", SiteCategory.ImmoPortal, "Recherche faite par le plugin, dans un onglet seloger.com ouvert.");
    public IListingParser Parser { get; } = new SelogerListingParser();
    public ISearchAdapter Search { get; } = new SelogerSearchAdapter(relay);

    IListingParser? ISiteModule.Parser => Parser;
    ISearchAdapter? ISiteModule.Search => Search;
}
