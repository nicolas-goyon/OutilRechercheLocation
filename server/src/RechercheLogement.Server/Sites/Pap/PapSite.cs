using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Pap;

/// <summary>
/// PAP (www.pap.fr), annonces de particulier à particulier. Recherche seulement (<see cref="PapSearchAdapter"/>) :
/// le site refuse les appels hors navigateur, la recherche passe par un onglet pap.fr ouvert avec le plugin.
/// Côté plugin : plugin/src/sites/pap/.
/// </summary>
public sealed class PapSite(BrowserRelay relay) : ISiteModule
{
    public const string Id = "pap";

    public SiteInfo Info { get; } = new(Id, "PAP", SiteCategory.ImmoPortal,
        "Particulier à particulier. Recherche faite par le plugin, dans un onglet pap.fr ouvert.", BrowserTab: true);
    public ISearchAdapter Search { get; } = new PapSearchAdapter(relay);

    IListingParser? ISiteModule.Parser => null;
    ISearchAdapter? ISiteModule.Search => Search;
}
