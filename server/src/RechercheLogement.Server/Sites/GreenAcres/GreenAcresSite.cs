using RechercheLogement.Core.Projects;

namespace RechercheLogement.Server.Sites.GreenAcres;

/// <summary>
/// Green-Acres (www.green-acres.fr) : portail d'annonces de vente, beaucoup de maisons de campagne. Recherche seulement
/// (<see cref="GreenAcresSearchAdapter"/>, lecture du HTML), pas de parser pour le plugin.
/// Client HTTP à part, sans cookies : le site mémorise la dernière recherche dans un cookie et la mêle à la suivante.
/// </summary>
public sealed class GreenAcresSite : ISiteModule
{
    public const string Id = "greenacres";

    public SiteInfo Info { get; } = new(Id, "Green-Acres", SiteCategory.ImmoPortal,
        "Annonces de vente (achat seulement), lues dans les pages du site ; une fiche lue par annonce (code postal, DPE).");
    public ISearchAdapter Search { get; } = new GreenAcresSearchAdapter(GreenAcresSearchAdapter.CreateClient());

    IListingParser? ISiteModule.Parser => null;
    ISearchAdapter? ISiteModule.Search => Search;
}
