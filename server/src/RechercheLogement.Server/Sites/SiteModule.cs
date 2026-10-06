using System.Text.Json.Nodes;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;

namespace RechercheLogement.Server.Sites;

// Un dossier par site d'annonces (Sites/<Site>/). Chaque dossier regroupe les fonctionnalités du site :
//   <Site>Site.cs            module : identité du site + fonctionnalités disponibles
//   <Site>ListingParser.cs   fonctionnalité "parser d'annonces" : données du site -> format commun
//   <Site>SearchAdapter.cs   fonctionnalité "adaptateur de recherche" : lieux + recherche avec les filtres du site
// Ajouter une fonctionnalité : une interface ici, une propriété dans ISiteModule, une classe par site.

/// <summary>Identité d'un site et sa catégorie.</summary>
public sealed record SiteInfo(string Id, string Label, SiteCategory Category, string Description);

/// <summary>Types de lieux qu'un site sait chercher directement.</summary>
[Flags]
public enum PlaceKinds
{
    None = 0,
    PostalCode = 1,
    Commune = 2,
    Department = 4,
    Region = 8,
    /// <summary>Rayon autour d'un point.</summary>
    Radius = 16,
    /// <summary>Zone dessinée (polygone).</summary>
    Polygon = 32,
}

public enum PlaceKind
{
    /// <summary>Texte libre saisi par l'utilisateur ("Rodez", "12850", "Aveyron").</summary>
    Auto,
    PostalCode,
    /// <summary>Texte = nom du département ("Aveyron").</summary>
    Department,
}

/// <summary>Un lieu à faire reconnaître par un site.</summary>
public sealed record PlaceQuery(PlaceKind Kind, string Text)
{
    /// <summary>Clé du cache des lieux du projet ("cp:12000", "dep:Aveyron", "rodez").</summary>
    public string CacheKey => Kind switch
    {
        PlaceKind.PostalCode => $"cp:{Text.Trim()}",
        PlaceKind.Department => $"dep:{Text.Trim()}",
        _ => Text.Trim().ToLowerInvariant(),
    };
}

/// <summary>Une annonce renvoyée par un site, convertie au format commun.</summary>
public sealed record CollectedItem(string SiteId, ListingData Data);

/// <param name="MaxResults">Nombre maximal d'annonces à récupérer (les plus récentes d'abord).</param>
public sealed record SearchRequest(SearchProject Project, IReadOnlyList<string> PlaceIds, int MaxResults);

/// <param name="Complete">true : toutes les annonces de la recherche ont été récupérées (sert à détecter les retraits).</param>
public sealed record SearchOutcome(IReadOnlyList<CollectedItem> Items, bool Complete, int Total);

/// <summary>Fonctionnalité "parser d'annonces" : une annonce brute du site (JSON) -> format commun.</summary>
public interface IListingParser
{
    CollectedItem? Parse(JsonNode raw);
}

/// <summary>Fonctionnalité "adaptateur de recherche" : reconnaître un lieu, chercher avec les filtres du site.</summary>
public interface ISearchAdapter
{
    PlaceKinds SupportedPlaces { get; }
    Task<CachedPlace?> ResolvePlaceAsync(PlaceQuery query, TransactionType transaction, CancellationToken ct);
    Task<SearchOutcome> SearchAsync(SearchRequest request, CancellationToken ct);
}

/// <summary>Un site d'annonces et ses fonctionnalités (null : pas encore disponible pour ce site).</summary>
public interface ISiteModule
{
    SiteInfo Info { get; }
    IListingParser? Parser { get; }
    ISearchAdapter? Search { get; }
}

/// <summary>Erreur lisible par l'utilisateur (affichée sur la page du projet).</summary>
public sealed class SiteException(string message, Exception? inner = null) : Exception(message, inner);
