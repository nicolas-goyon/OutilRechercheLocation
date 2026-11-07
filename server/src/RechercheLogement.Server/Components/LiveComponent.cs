using Microsoft.AspNetCore.Components;
using RechercheLogement.Core.Services;
using RechercheLogement.Server.Photos;
using RechercheLogement.Server.Projects;

namespace RechercheLogement.Server.Components;

/// <summary>
/// Classe de base des pages. La page se met à jour quand le catalogue change.
/// Exemple : si le plugin masque une annonce, la page affiche immédiatement ce changement.
/// Elle se recharge aussi quand on change de projet.
/// </summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    [Inject] protected Catalog Catalog { get; set; } = default!;
    /// <summary>Projet choisi dans la barre latérale : les pages limitent leurs données à son périmètre.</summary>
    [Inject] protected ProjectSelection Selection { get; set; } = default!;
    /// <summary>Téléchargement des photos en tâche de fond : les pages disent ce qu'elles affichent.</summary>
    [Inject] protected PhotoPrefetcher Prefetch { get; set; } = default!;

    protected override void OnInitialized()
    {
        Catalog.Changed += OnCatalogChanged;
        Selection.Changed += OnCatalogChanged;
        Prefetch.UseProject(Selection.ProjectId);
        Load();
    }

    /// <summary>Biens affichés par la page, dans l'ordre : leurs photos sont téléchargées en premier.</summary>
    protected void ShowPhotos(IEnumerable<string> propertyIds) => Prefetch.ShowList(propertyIds, Selection.ProjectId);

    /// <summary>Recharge les données de la page depuis le catalogue.</summary>
    protected abstract void Load();

    private void OnCatalogChanged() => _ = InvokeAsync(() =>
    {
        Prefetch.UseProject(Selection.ProjectId);
        Load();
        StateHasChanged();
    });

    public virtual void Dispose()
    {
        Catalog.Changed -= OnCatalogChanged;
        Selection.Changed -= OnCatalogChanged;
        GC.SuppressFinalize(this);
    }
}
