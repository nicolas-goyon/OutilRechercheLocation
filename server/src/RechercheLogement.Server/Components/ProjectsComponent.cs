using Microsoft.AspNetCore.Components;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Projects;
using RechercheLogement.Server.Sites;

namespace RechercheLogement.Server.Components;

/// <summary>Classe de base des pages "Mes projets" : la page se met à jour quand un projet change (lancement en cours, nouveaux résultats).</summary>
public abstract class ProjectsComponent : ComponentBase, IDisposable
{
    [Inject] protected ProjectStore Store { get; set; } = default!;
    [Inject] protected ProjectRunner Runner { get; set; } = default!;
    [Inject] protected SiteRegistry Sites { get; set; } = default!;

    protected override void OnInitialized()
    {
        Store.Changed += OnChanged;
        Load();
    }

    /// <summary>Recharge les données de la page depuis le stockage des projets.</summary>
    protected abstract void Load();

    private void OnChanged() => _ = InvokeAsync(() =>
    {
        Load();
        StateHasChanged();
    });

    /// <summary>Lance le projet en arrière-plan. La page suit l'avancement par l'événement Changed.</summary>
    protected void StartRun(string projectId) => _ = Task.Run(() => Runner.RunAsync(projectId, CancellationToken.None));

    public virtual void Dispose()
    {
        Store.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}

/// <summary>Libellés des pages "Mes projets".</summary>
public static class ProjectFormat
{
    public static string Types(IEnumerable<string> types) => string.Join(", ", types.Select(t => t switch
    {
        "flat" => "Appartement",
        "house" => "Maison",
        _ => t,
    }));

    /// <summary>"Location · Appartement · Rodez, Onet-le-Château · 400 à 700 € · ≥ 40 m² · ≥ 2 pièces".</summary>
    public static string Summary(SearchProject p)
    {
        var parts = new List<string>
        {
            p.Transaction == TransactionType.Buy ? "Achat" : "Location",
            Types(p.PropertyTypes),
            Zone(p),
        };
        if (Range(p.PriceMin, p.PriceMax, "€") is { } price) parts.Add(price);
        if (Range(p.SurfaceMin, p.SurfaceMax, "m²") is { } surface) parts.Add(surface);
        if (Range(p.RoomsMin, p.RoomsMax, "pièces") is { } rooms) parts.Add(rooms);
        if (p.BedroomsMin is { } b) parts.Add($"≥ {b} chambre(s)");
        if (p.Furnished is { } f) parts.Add(f ? "meublé" : "non meublé");
        if (p.KeywordsInclude.Count > 0) parts.Add($"avec : {string.Join(", ", p.KeywordsInclude)}");
        if (p.KeywordsExclude.Count > 0) parts.Add($"sans : {string.Join(", ", p.KeywordsExclude)}");
        return string.Join(" · ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string? Range<T>(T? min, T? max, string unit) where T : struct, IFormattable
    {
        static string F(T v) => v.ToString("#,0.##", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
        return (min, max) switch
        {
            ({ } a, { } b) => $"{F(a)} à {F(b)} {unit}",
            ({ } a, null) => $"≥ {F(a)} {unit}",
            (null, { } b) => $"≤ {F(b)} {unit}",
            _ => null,
        };
    }

    /// <summary>"Rodez, 12850" · "10 km autour de Rodez" · "30 min en voiture depuis 8 Boulevard Gally 12000 Rodez".</summary>
    public static string Zone(SearchProject p) => p.LocationMode switch
    {
        LocationMode.Radius => $"{p.RadiusKm.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} km autour de {p.CenterLabel ?? p.CenterQuery}",
        LocationMode.TravelTime => $"{p.TravelMinutes} min {Travel(p.TravelBy)} depuis {p.CenterLabel ?? p.CenterQuery}",
        _ => string.Join(", ", p.Locations.Select(l => l.Query)),
    };

    public static string Travel(TravelMode m) => m switch { TravelMode.Bike => "à vélo", TravelMode.Walk => "à pied", _ => "en voiture" };

    public static string Distance(double? meters) => meters switch
    {
        null => "",
        < 1000 => $"à {Math.Round(meters.Value / 100) * 100:0} m",
        _ => $"à {(meters.Value / 1000).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} km",
    };

    public static string Refresh(int hours) => hours switch
    {
        <= 0 => "à la demande",
        1 => "toutes les heures",
        24 => "tous les jours",
        _ => $"toutes les {hours} h",
    };
}
