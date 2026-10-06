using RechercheLogement.Core.Model;

namespace RechercheLogement.Core.Contracts;

// Contrat de /api/searches (recherches favorites). Le plugin l'utilise depuis le panneau 🏠
// ("Enregistrer cette recherche"). Copie de SavedSearchRequest / SavedSearchView dans plugin/src/core/types.ts.

/// <summary>Nouvelle recherche favorite. <see cref="Name"/> est optionnel (nom par défaut : "Recherche &lt;site&gt;").</summary>
public sealed record SavedSearchRequest(string? Name, string? Url, string? Note = null);

public sealed record SavedSearchView(string Id, string Name, string Url, string Site, string? Note, long CreatedAt, long? LastOpenedAt)
{
    public static SavedSearchView Of(SavedSearch s) => new(s.Id, s.Name, s.Url, s.Site, s.Note, s.CreatedAt, s.LastOpenedAt);
}
