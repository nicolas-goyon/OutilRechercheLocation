using RechercheLogement.Core.Projects;

namespace RechercheLogement.Server.Sites.Bienici;

/// <summary>
/// Bien'ici (www.bienici.com). Fonctionnalités :
///  - <see cref="BieniciListingParser"/> : annonce de l'API (realEstateAds.json) -> format commun.
///  - <see cref="BieniciSearchAdapter"/> : lieux (suggest.json) + recherche (realEstateAds.json).
/// Côté plugin (navigateur) : plugin/src/sites/bienici/.
/// </summary>
public sealed class BieniciSite(IHttpClientFactory http) : ISiteModule
{
    public const string Id = "bienici";

    public SiteInfo Info { get; } = new(Id, "Bien'ici", SiteCategory.ImmoPortal, "API de recherche publique du site.");
    public IListingParser Parser { get; } = new BieniciListingParser();
    public ISearchAdapter Search { get; } = new BieniciSearchAdapter(http);

    IListingParser? ISiteModule.Parser => Parser;
    ISearchAdapter? ISiteModule.Search => Search;
}
