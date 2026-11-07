using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Leboncoin;

/// <summary>
/// Leboncoin (www.leboncoin.fr), catégorie « Ventes immobilières » / « Locations ». Fonctionnalités :
///  - <see cref="LeboncoinListingParser"/> : annonce du JSON des pages -> format commun.
///  - <see cref="LeboncoinSearchAdapter"/> : lieux (sans appel au site) + recherche (pages de résultats).
/// Le site refuse les appels hors navigateur : la recherche passe par un onglet leboncoin.fr ouvert avec le plugin.
/// Côté plugin : plugin/src/sites/leboncoin/.
/// </summary>
public sealed class LeboncoinSite(BrowserRelay relay) : ISiteModule
{
    public const string Id = "leboncoin";

    public SiteInfo Info { get; } = new(Id, "Leboncoin", SiteCategory.GeneralClassifieds,
        "Recherche faite par le plugin, dans un onglet leboncoin.fr ouvert. Beaucoup de particuliers.", BrowserTab: true);
    public IListingParser Parser { get; } = new LeboncoinListingParser();
    public ISearchAdapter Search { get; } = new LeboncoinSearchAdapter(relay);

    IListingParser? ISiteModule.Parser => Parser;
    ISearchAdapter? ISiteModule.Search => Search;
}
