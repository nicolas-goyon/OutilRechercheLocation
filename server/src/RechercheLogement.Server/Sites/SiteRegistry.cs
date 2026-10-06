using RechercheLogement.Core.Projects;

namespace RechercheLogement.Server.Sites;

/// <summary>Un site tel qu'affiché dans les projets : disponible (module avec recherche) ou "à venir".</summary>
public sealed record SiteEntry(SiteInfo Info, ISiteModule? Module)
{
    public bool Available => Module?.Search is not null;
}

/// <summary>Tous les sites : modules enregistrés (Sites/&lt;Site&gt;/) + sites prévus, affichés "à venir".</summary>
public sealed class SiteRegistry(IEnumerable<ISiteModule> modules)
{
    /// <summary>Sites prévus mais pas encore branchés.</summary>
    public static readonly IReadOnlyList<SiteInfo> Upcoming =
    [
        new("pap", "PAP", SiteCategory.ImmoPortal, "À venir."),
        new("logicimmo", "Logic-Immo", SiteCategory.ImmoPortal, "À venir."),
        new("leboncoin", "Leboncoin", SiteCategory.GeneralClassifieds, "À venir."),
        new("agences", "Sites d'agences", SiteCategory.Agency, "À venir : une entrée par agence suivie."),
    ];

    private readonly IReadOnlyList<SiteEntry> _all =
        modules.Select(m => new SiteEntry(m.Info, m))
            .Concat(Upcoming.Where(u => modules.All(m => m.Info.Id != u.Id)).Select(u => new SiteEntry(u, null)))
            .ToList();

    public IReadOnlyList<SiteEntry> All => _all;

    public SiteEntry? Get(string id) => _all.FirstOrDefault(s => s.Info.Id == id);

    public string Label(string id) => Get(id)?.Info.Label ?? id;

    public static string CategoryLabel(SiteCategory c) => c switch
    {
        SiteCategory.ImmoPortal => "Sites d'annonces immobilières",
        SiteCategory.GeneralClassifieds => "Sites d'annonces généralistes",
        SiteCategory.Agency => "Sites d'agences",
        _ => c.ToString(),
    };
}
