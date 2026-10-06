using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Model;

namespace RechercheLogement.Core.Projects;

// "Mes projets" : recherches complètes (critères + sites) que le serveur lance lui-même sur les sites
// d'annonces. Données séparées du catalogue du plugin (annonces, biens, doublons) : autre base, autre service.

/// <summary>Catégorie d'un site interrogé par un projet.</summary>
public enum SiteCategory
{
    /// <summary>Portail immobilier (Bien'ici, SeLoger, PAP...).</summary>
    ImmoPortal,
    /// <summary>Site d'annonces généraliste (Leboncoin...).</summary>
    GeneralClassifieds,
    /// <summary>Site d'une agence immobilière.</summary>
    Agency,
}

/// <param name="Available">false : site affiché mais pas encore interrogé ("à venir").</param>
public sealed record SourceSite(string Id, string Label, SiteCategory Category, bool Available, string Description);

/// <summary>Sites connus. Ajouter un site : une entrée ici + un collecteur (server/.../Projects/Collectors).</summary>
public static class SourceSites
{
    public static readonly IReadOnlyList<SourceSite> All =
    [
        new("bienici", "Bien'ici", SiteCategory.ImmoPortal, true, "API de recherche publique du site."),
        new("seloger", "SeLoger", SiteCategory.ImmoPortal, true, "API de recherche du site. Peut être bloquée par sa protection anti-robot."),
        new("pap", "PAP", SiteCategory.ImmoPortal, false, "À venir."),
        new("logicimmo", "Logic-Immo", SiteCategory.ImmoPortal, false, "À venir."),
        new("leboncoin", "Leboncoin", SiteCategory.GeneralClassifieds, false, "À venir."),
        new("agences", "Sites d'agences", SiteCategory.Agency, false, "À venir : une entrée par agence suivie."),
    ];

    public static SourceSite? Get(string id) => All.FirstOrDefault(s => s.Id == id);

    public static string Label(SiteCategory c) => c switch
    {
        SiteCategory.ImmoPortal => "Sites d'annonces immobilières",
        SiteCategory.GeneralClassifieds => "Sites d'annonces généralistes",
        SiteCategory.Agency => "Sites d'agences",
        _ => c.ToString(),
    };
}

/// <summary>Un lieu du projet, tel que saisi ("Rodez", "12850"), et ses identifiants sur chaque site (résolus puis gardés).</summary>
public sealed class ProjectLocation
{
    public string Query { get; set; } = "";
    /// <summary>site -> libellé trouvé sur ce site ("Rodez (12000)").</summary>
    public Dictionary<string, string> Labels { get; set; } = [];
    /// <summary>site -> identifiants du lieu sur ce site (zoneIds Bien'ici, placeIds SeLoger...).</summary>
    public Dictionary<string, List<string>> SiteIds { get; set; } = [];
}

public sealed class SearchProject
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public TransactionType Transaction { get; set; } = TransactionType.Rent;
    /// <summary>Types communs : "flat", "house" (mêmes valeurs que ListingData.PropertyType).</summary>
    public List<string> PropertyTypes { get; set; } = ["flat"];
    public List<ProjectLocation> Locations { get; set; } = [];
    public decimal? PriceMin { get; set; }
    public decimal? PriceMax { get; set; }
    public double? SurfaceMin { get; set; }
    public double? SurfaceMax { get; set; }
    public int? RoomsMin { get; set; }
    public int? RoomsMax { get; set; }
    public int? BedroomsMin { get; set; }
    /// <summary>null : indifférent.</summary>
    public bool? Furnished { get; set; }
    /// <summary>Au moins un de ces mots doit apparaître dans le titre ou la description (vide : pas de contrainte).</summary>
    public List<string> KeywordsInclude { get; set; } = [];
    /// <summary>Aucun de ces mots ne doit apparaître.</summary>
    public List<string> KeywordsExclude { get; set; } = [];
    /// <summary>Sites interrogés (identifiants de <see cref="SourceSites"/>).</summary>
    public List<string> Sites { get; set; } = ["bienici", "seloger"];
    /// <summary>Lancement automatique toutes les N heures. 0 : seulement à la demande.</summary>
    public int AutoRefreshHours { get; set; }
    public bool Active { get; set; } = true;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long? LastRunAt { get; set; }
    /// <summary>Dernière visite de la page des résultats. Les annonces trouvées après sont "nouvelles".</summary>
    public long? LastViewedAt { get; set; }

    public static string NewId() => $"pr_{Guid.NewGuid():N}"[..15];

    public bool IsDue(long now) =>
        Active && AutoRefreshHours > 0 && (LastRunAt is null || now - LastRunAt.Value >= AutoRefreshHours * 3_600_000L);
}

/// <summary>Une annonce trouvée pour un projet.</summary>
public sealed class ProjectResult
{
    public required string ProjectId { get; init; }
    public required string Site { get; init; }
    public required string SiteId { get; init; }
    public string Key => $"{Site}:{SiteId}";
    public ListingData Data { get; set; } = new();
    public long FirstSeenAt { get; set; }
    public long LastSeenAt { get; set; }
    /// <summary>Masquée par l'utilisateur sur la page du projet.</summary>
    public bool Hidden { get; set; }
}

/// <summary>Dernier passage d'un projet sur un site.</summary>
public sealed record ProjectRun(string ProjectId, string Site, long At, bool Ok, int Found, int New, string Message);

/// <summary>Vérifie une annonce contre les critères du projet. Les sites appliquent déjà une partie des filtres ; un critère inconnu sur l'annonce ne l'exclut pas.</summary>
public static class ProjectMatcher
{
    public static bool Matches(SearchProject p, ListingData d, out string? why)
    {
        why = null;
        if (d.Transaction is not null && d.Transaction != p.Transaction) return Fail("transaction", out why);
        if (d.PropertyType is not null && p.PropertyTypes.Count > 0 && !p.PropertyTypes.Contains(d.PropertyType)) return Fail("type de bien", out why);
        if (d.Price is not null && (d.Price < p.PriceMin || d.Price > p.PriceMax)) return Fail("prix", out why);
        if (d.Surface is not null && (d.Surface < p.SurfaceMin || d.Surface > p.SurfaceMax)) return Fail("surface", out why);
        if (d.Rooms is not null && (d.Rooms < p.RoomsMin || d.Rooms > p.RoomsMax)) return Fail("pièces", out why);
        if (d.Bedrooms is not null && d.Bedrooms < p.BedroomsMin) return Fail("chambres", out why);
        if (p.Furnished is not null && d.Furnished is not null && d.Furnished != p.Furnished) return Fail("meublé", out why);

        var text = DedupScorer.NormalizeText($"{d.Title} {d.DescriptionExcerpt}");
        bool Has(string kw) => DedupScorer.NormalizeText(kw) is { Length: > 0 } k && $" {text} ".Contains($" {k} ", StringComparison.Ordinal);
        var include = p.KeywordsInclude.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        if (include.Count > 0 && !include.Any(Has)) return Fail("mots-clés absents", out why);
        if (p.KeywordsExclude.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k) && Has(k)) is { } excluded) return Fail($"mot exclu « {excluded} »", out why);
        return true;
    }

    private static bool Fail(string reason, out string? why)
    {
        why = reason;
        return false;
    }
}
