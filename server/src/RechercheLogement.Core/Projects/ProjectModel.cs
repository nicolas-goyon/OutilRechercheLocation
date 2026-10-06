using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Geo;
using RechercheLogement.Core.Model;

namespace RechercheLogement.Core.Projects;

// "Mes projets" : recherches complètes (critères + zone + sites) que le serveur lance lui-même sur les
// sites d'annonces. Données séparées du catalogue du plugin (annonces, biens, doublons) : autre base,
// autre service. Le catalogue des sites (et ce que chaque site sait faire) est côté serveur : Sites/.

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

// ------------------------------------------------------------------ zone de recherche

/// <summary>Façon de décrire la zone de recherche.</summary>
public enum LocationMode
{
    /// <summary>Liste de villes, codes postaux ou départements, transmis tels quels aux sites.</summary>
    Places,
    /// <summary>Rayon (km) autour d'un point.</summary>
    Radius,
    /// <summary>Temps de trajet (minutes) autour d'un point.</summary>
    TravelTime,
}

public enum TravelMode { Car, Bike, Walk }

/// <summary>Un lieu saisi en mode <see cref="LocationMode.Places"/> ("Rodez", "12850", "Aveyron").</summary>
public sealed class ProjectLocation
{
    public string Query { get; set; } = "";
}

/// <summary>
/// Une commune d'un département touché par la zone, avec son centre. <paramref name="InArea"/> : centre dans
/// la zone (avec une marge). Les autres servent à situer (et écarter) les annonces des communes voisines.
/// </summary>
public sealed record PlanCommune(string Code, string Name, List<string> PostalCodes, string Department, GeoPoint Center, bool InArea);

/// <summary>
/// Zone calculée pour les modes rayon et temps de trajet : forme exacte, communes et départements couverts.
/// Les sites ne savent pas chercher dans un cercle ou un isochrone : on leur demande les codes postaux
/// (ou les départements) qui couvrent la zone, puis on garde les annonces situées dans la forme exacte.
/// Gardée dans le projet, recalculée si la zone change (<see cref="Fingerprint"/>) ou tous les 30 jours.
/// </summary>
public sealed class AreaPlan
{
    public string Fingerprint { get; set; } = "";
    public long ComputedAt { get; set; }
    public GeoShape Shape { get; set; } = new();
    /// <summary>true : forme approchée (isochrone indisponible, cercle estimé à partir de la vitesse moyenne).</summary>
    public bool Approximate { get; set; }
    public string? Note { get; set; }
    public List<PlanCommune> Communes { get; set; } = [];
    /// <summary>Code département -> nom ("12" -> "Aveyron").</summary>
    public Dictionary<string, string> Departments { get; set; } = [];

    /// <summary>Codes postaux des communes de la zone : la recherche la plus fine possible sur les sites.</summary>
    public List<string> PostalCodes() => Communes.Where(c => c.InArea).SelectMany(c => c.PostalCodes).Distinct().Order(StringComparer.Ordinal).ToList();
}

/// <summary>Lieu reconnu par un site, gardé en cache dans le projet (évite de redemander à chaque lancement).</summary>
public sealed record CachedPlace(string Label, List<string> Ids);

// ------------------------------------------------------------------ projet

public sealed class SearchProject
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public TransactionType Transaction { get; set; } = TransactionType.Rent;
    /// <summary>Types communs : "flat", "house" (mêmes valeurs que ListingData.PropertyType).</summary>
    public List<string> PropertyTypes { get; set; } = ["flat"];

    public LocationMode LocationMode { get; set; } = LocationMode.Places;
    /// <summary>Mode <see cref="LocationMode.Places"/>.</summary>
    public List<ProjectLocation> Locations { get; set; } = [];
    /// <summary>Modes rayon / temps de trajet : adresse ou ville saisie.</summary>
    public string? CenterQuery { get; set; }
    /// <summary>Point trouvé pour <see cref="CenterQuery"/> (géocodage), et son libellé.</summary>
    public GeoPoint? Center { get; set; }
    public string? CenterLabel { get; set; }
    public double RadiusKm { get; set; } = 10;
    public int TravelMinutes { get; set; } = 30;
    public TravelMode TravelBy { get; set; } = TravelMode.Car;
    /// <summary>Zone calculée (modes rayon / temps de trajet).</summary>
    public AreaPlan? Area { get; set; }
    /// <summary>site -> requête de lieu ("12000", "dep:12", "Rodez") -> lieu reconnu par le site. null : non reconnu.</summary>
    public Dictionary<string, Dictionary<string, CachedPlace?>> PlaceCache { get; set; } = [];

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
    /// <summary>Sites interrogés (identifiants des modules de Sites/).</summary>
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

    /// <summary>Empreinte de la zone (modes rayon / temps de trajet) : une zone modifiée doit être recalculée.</summary>
    public string AreaFingerprint() => LocationMode switch
    {
        LocationMode.Radius => $"radius|{CenterQuery?.Trim().ToLowerInvariant()}|{RadiusKm}",
        LocationMode.TravelTime => $"travel|{CenterQuery?.Trim().ToLowerInvariant()}|{TravelMinutes}|{TravelBy}",
        _ => "",
    };
}

// ------------------------------------------------------------------ résultats et suivi

public enum ResultEventKind
{
    /// <summary>Première apparition de l'annonce.</summary>
    Appeared,
    /// <summary>Prix, surface, pièces, meublé, agence ou texte modifiés.</summary>
    Changed,
    /// <summary>Absente d'une recherche complète du site : retirée (ou louée/vendue).</summary>
    Removed,
    /// <summary>De nouveau présente après un retrait.</summary>
    Reappeared,
    /// <summary>Reconnue comme le même bien qu'une autre annonce du projet (même site ou autre site).</summary>
    Linked,
}

public sealed record ResultEvent(long At, ResultEventKind Kind, string Text);

/// <summary>Une annonce trouvée pour un projet, avec son historique.</summary>
public sealed class ProjectResult
{
    public required string ProjectId { get; init; }
    public required string Site { get; init; }
    public required string SiteId { get; init; }
    public string Key => $"{Site}:{SiteId}";
    public ListingData Data { get; set; } = new();
    public long FirstSeenAt { get; set; }
    public long LastSeenAt { get; set; }
    /// <summary>Retirée du site (absente de la dernière recherche complète).</summary>
    public bool Removed { get; set; }
    /// <summary>Masquée par l'utilisateur sur la page du projet.</summary>
    public bool Hidden { get; set; }
    /// <summary>Groupe = même bien. Clé de la première annonce du groupe.</summary>
    public string GroupId { get; set; } = "";
    /// <summary>Distance au centre de la zone (modes rayon / temps de trajet), en mètres.</summary>
    public double? DistanceM { get; set; }
    public List<ResultEvent> History { get; set; } = [];
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

/// <summary>
/// Position d'une annonce dans la zone : GPS de l'annonce si le site le donne, sinon centre de sa commune
/// (code postal + nom, d'après les communes de la zone).
/// </summary>
public static class AreaFilter
{
    /// <summary>Marge autour de la zone : flou GPS des sites, communes au bord de la zone.</summary>
    public const double DefaultToleranceM = 500;

    public enum Verdict { Inside, Outside, Unknown }

    public static Verdict Check(AreaPlan plan, ListingData d, out double? distanceM)
    {
        distanceM = null;
        var (point, precision) = Locate(plan, d);
        if (point is null) return Verdict.Unknown;
        var center = plan.Shape.Center;
        if (center is not null) distanceM = DedupScorer.DistanceMeters(center, point);
        return plan.Shape.Contains(point, DefaultToleranceM + precision) ? Verdict.Inside : Verdict.Outside;
    }

    public static (GeoPoint? Point, double PrecisionM) Locate(AreaPlan plan, ListingData d)
    {
        if (d.Geo is not null) return (d.Geo, d.Geo.PrecisionM ?? 0);
        var city = d.City is null ? null : DedupScorer.NormalizeText(d.City);
        var candidates = plan.Communes.Where(c => d.PostalCode is not null && c.PostalCodes.Contains(d.PostalCode)).ToList();
        var byName = city is null ? null
            : (candidates.Count > 0 ? candidates : plan.Communes).FirstOrDefault(c => DedupScorer.NormalizeText(c.Name) == city);
        var commune = byName ?? (candidates.Count == 1 ? candidates[0] : null);
        // Commune trouvée : son centre, avec une marge de 2 km (la commune n'est pas un point).
        return commune is null ? (null, 0) : (commune.Center, 2000);
    }
}
