using Microsoft.AspNetCore.Components;
using RechercheLogement.Core.Services;

namespace RechercheLogement.Server.Components;

/// <summary>
/// Base des pages : se rafraîchit toute seule quand le catalogue change
/// (ex. une annonce masquée depuis le plugin apparaît immédiatement ici).
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
