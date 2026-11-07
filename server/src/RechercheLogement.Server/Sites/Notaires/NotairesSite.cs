using RechercheLogement.Core.Projects;

namespace RechercheLogement.Server.Sites.Notaires;

/// <summary>
/// Immobilier des Notaires de France (www.immobilier.notaires.fr) : annonces des offices notariaux, ventes classiques,
/// Immo-Interactif (offres en ligne), enchères et locations. Recherche seulement (<see cref="NotairesSearchAdapter"/>),
/// pas de parser pour le plugin. <paramref name="delayMs"/> : délai entre deux requêtes (robots.txt : 10 s).
/// </summary>
public sealed class NotairesSite(IHttpClientFactory http, int delayMs = NotairesSearchAdapter.DefaultDelayMs) : ISiteModule, IDisposable
{
    public const string Id = "notaires";

    public SiteInfo Info { get; } = new(Id, "Notaires de France", SiteCategory.ImmoPortal,
        "API publique du site des notaires. Lent à dessein (10 s entre deux requêtes, demandé par le site) : une fiche est lue par annonce.");
    public NotairesSearchAdapter Search { get; } = new(http, delayMs);

    public void Dispose() => Search.Dispose();

    IListingParser? ISiteModule.Parser => null;
    ISearchAdapter? ISiteModule.Search => Search;
}
