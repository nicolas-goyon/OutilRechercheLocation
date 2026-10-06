using Microsoft.AspNetCore.Components;
using RechercheLogement.Core.Services;

namespace RechercheLogement.Server.Components;

/// <summary>
/// Classe de base des pages. La page se met à jour quand le catalogue change.
/// Exemple : si le plugin masque une annonce, la page affiche immédiatement ce changement.
/// </summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    [Inject] protected Catalog Catalog { get; set; } = default!;

    protected override void OnInitialized()
    {
        Catalog.Changed += OnCatalogChanged;
        Load();
    }

    /// <summary>Recharge les données de la page depuis le catalogue.</summary>
    protected abstract void Load();

    private void OnCatalogChanged() => _ = InvokeAsync(() =>
    {
        Load();
        StateHasChanged();
    });

    public virtual void Dispose()
    {
        Catalog.Changed -= OnCatalogChanged;
        GC.SuppressFinalize(this);
    }
}
