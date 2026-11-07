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
    /// <summary>Ventes publiques (cessions de l'État...).</summary>
    PublicSales,
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

/// <summary>Nature d'un lieu du mode <see cref="LocationMode.Places"/>.</summary>
public enum PlaceType
{
    /// <summary>Texte libre (anciens projets) : transmis tel quel aux sites.</summary>
    Free,
    Commune,
    PostalCode,
    Department,
    Region,
}

/// <summary>
/// Un lieu du mode <see cref="LocationMode.Places"/>, choisi dans les suggestions (commune, code postal,
/// département) ou saisi librement. <see cref="Query"/> est le texte demandé aux sites.
/// </summary>
public sealed class ProjectLocation
{
    /// <summary>Texte demandé aux sites : nom de la commune ou du département, code postal.</summary>
    public string Query { get; set; } = "";
    public PlaceType Type { get; set; }
    /// <summary>Code INSEE de la commune, code postal ou code du département.</summary>
    public string? Code { get; set; }
    /// <summary>Libellé affiché ("Rodez (12000)", "Aveyron (12)").</summary>
    public string? Label { get; set; }
    /// <summary>Codes postaux de la commune (aide les sites à choisir la bonne commune en cas d'homonymes).</summary>
    public List<string> PostalCodes { get; set; } = [];
    /// <summary>
    /// Commune nouvelle : noms des anciennes communes (déléguées, associées), que les sites et les annonces utilisent
    /// encore ("Vicdessos" pour Val-de-Sos). null : pas encore vérifié ; vide : aucune.
    /// </summary>
    public List<string>? FormerNames { get; set; }
    /// <summary>Centre de la commune (ou de la première commune du code postal), pour la carte.</summary>
    public GeoPoint? Center { get; set; }
    /// <summary>Région : noms de ses départements. Les sites reçoivent ces départements.</summary>
    public List<string> Departments { get; set; } = [];
    /// <summary>Région : codes de ses départements (rattachement des annonces au projet).</summary>
    public List<string> DepartmentCodes { get; set; } = [];

    public string Display() => string.IsNullOrWhiteSpace(Label) ? Query : Label;

    /// <summary>Clé d'unicité dans un projet ("commune:12202", "free:rodez").</summary>
    public string Key() => $"{Type}:{(Code ?? Query).Trim()}".ToLowerInvariant();
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
    /// <summary>Types communs (<see cref="Model.PropertyTypes"/>) : "flat", "house", "terrain"…</summary>
    public List<string> PropertyTypes { get; set; } = ["flat"];

    public LocationMode LocationMode { get; set; } = LocationMode.Places;
    /// <summary>Mode <see cref="LocationMode.Places"/>.</summary>
    public List<ProjectLocation> Locations { get; set; } = [];
    /// <summary>Modes rayon / temps de trajet : adresse ou ville saisie.</summary>
    public string? CenterQuery { get; set; }
    /// <summary>
    /// Point de départ : choisi dans les suggestions ou sur la carte, sinon trouvé au lancement par géocodage de
    /// <see cref="CenterQuery"/>. Son libellé est <see cref="CenterLabel"/>.
    /// </summary>
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
    /// <summary>Surface du terrain, en m².</summary>
    public double? LandSurfaceMin { get; set; }
    public double? LandSurfaceMax { get; set; }
    /// <summary>DPE le moins bon accepté ("D" : A à D). null : indifférent.</summary>
    public string? EnergyClassMax { get; set; }
    public int? YearBuiltMin { get; set; }
    public int? YearBuiltMax { get; set; }
    /// <summary>Salles de bain et salles d'eau.</summary>
    public int? BathroomsMin { get; set; }
    /// <summary>Équipements exigés (<see cref="Model.Features"/>).</summary>
    public List<string> RequiredFeatures { get; set; } = [];
    /// <summary>true : neuf seulement ; false : ancien seulement ; null : indifférent.</summary>
    public bool? NewBuild { get; set; }
    /// <summary>true : à rénover seulement ; false : sans travaux annoncés ; null : indifférent.</summary>
    public bool? WorkNeeded { get; set; }
    /// <summary>Écarte les viagers (les sites les mêlent aux ventes classiques). Vrai par défaut.</summary>
    public bool ExcludeLifeAnnuity { get; set; } = true;
    /// <summary>
    /// Critères obligatoires (<see cref="Criteria"/>) : une annonce qui ne précise pas l'information est écartée.
    /// Les autres critères n'écartent que les annonces qui la précisent et ne correspondent pas.
    /// </summary>
    public List<string> StrictCriteria { get; set; } = [];

    /// <summary>Le critère est-il obligatoire (et renseigné) ?</summary>
    public bool IsStrict(string criterion) => StrictCriteria.Contains(criterion);
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
        LocationMode.Radius => FormattableString.Invariant($"radius|{CenterKey()}|{RadiusKm}"),
        LocationMode.TravelTime => $"travel|{CenterKey()}|{TravelMinutes}|{TravelBy}",
        _ => "",
    };

    /// <summary>Point de départ : coordonnées si connues (suggestion, clic sur la carte, géocodage), sinon texte saisi.</summary>
    private string CenterKey() => Center is { } c
        ? FormattableString.Invariant($"{c.Lat:0.#####},{c.Lon:0.#####}")
        : CenterQuery?.Trim().ToLowerInvariant() ?? "";
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
        if (PropertyTypes.Normalize(d.PropertyType) is { } type && p.PropertyTypes.Count > 0 && !p.PropertyTypes.Select(PropertyTypes.Normalize).Contains(type)) return Fail("type de bien", out why);
        if (d.Price is not null && (d.Price < p.PriceMin || d.Price > p.PriceMax)) return Fail("prix", out why);
        if (d.Surface is not null && (d.Surface < p.SurfaceMin || d.Surface > p.SurfaceMax)) return Fail("surface", out why);
        if (d.Rooms is not null && (d.Rooms < p.RoomsMin || d.Rooms > p.RoomsMax)) return Fail("pièces", out why);
        if (Check(p, Criteria.Bedrooms, p.BedroomsMin is not null, d.Bedrooms is null, d.Bedrooms < p.BedroomsMin, "chambres", "nombre de chambres", out why) is false) return false;
        if (p.Furnished is not null && d.Furnished is not null && d.Furnished != p.Furnished) return Fail("meublé", out why);
        if (Check(p, Criteria.LandSurface, p.LandSurfaceMin is not null || p.LandSurfaceMax is not null, d.LandSurface is null,
                d.LandSurface < p.LandSurfaceMin || d.LandSurface > p.LandSurfaceMax, "terrain", "terrain", out why) is false) return false;
        var energy = EnergyClasses.Normalize(d.EnergyClass);
        if (Check(p, Criteria.EnergyClass, p.EnergyClassMax is not null, energy is null, EnergyClasses.Worse(energy, p.EnergyClassMax), $"DPE {energy}", "DPE", out why) is false) return false;
        if (Check(p, Criteria.YearBuilt, p.YearBuiltMin is not null || p.YearBuiltMax is not null, d.YearBuilt is null,
                d.YearBuilt < p.YearBuiltMin || d.YearBuilt > p.YearBuiltMax, "année de construction", "année de construction", out why) is false) return false;
        if (Check(p, Criteria.Bathrooms, p.BathroomsMin is not null, d.Bathrooms is null, d.Bathrooms < p.BathroomsMin, "salles de bain", "salles de bain", out why) is false) return false;
        if (p.RequiredFeatures.FirstOrDefault(f => d.Has(f) == false) is { } missing) return Fail($"sans {Features.Label(missing).ToLowerInvariant()}", out why);
        if (p.IsStrict(Criteria.Features) && p.RequiredFeatures.FirstOrDefault(f => d.Has(f) is null) is { } unknown)
            return Fail($"{Features.Label(unknown).ToLowerInvariant()} : non mentionné", out why);
        if (Check(p, Criteria.NewBuild, p.NewBuild is not null, d.NewBuild is null, d.NewBuild != p.NewBuild, p.NewBuild == true ? "ancien" : "neuf", "neuf ou ancien", out why) is false) return false;
        if (Check(p, Criteria.WorkNeeded, p.WorkNeeded is not null, d.WorkNeeded is null, d.WorkNeeded != p.WorkNeeded,
                p.WorkNeeded == true ? "sans travaux" : "travaux à prévoir", "état (travaux)", out why) is false) return false;
        if (p.ExcludeLifeAnnuity && d.SaleKind == SaleKinds.LifeAnnuity) return Fail("viager", out why);

        var text = DedupScorer.NormalizeText($"{d.Title} {d.DescriptionExcerpt}");
        bool Has(string kw) => DedupScorer.NormalizeText(kw) is { Length: > 0 } k && $" {text} ".Contains($" {k} ", StringComparison.Ordinal);
        var include = p.KeywordsInclude.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        if (include.Count > 0 && !include.Any(Has)) return Fail("mots-clés absents", out why);
        if (p.KeywordsExclude.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k) && Has(k)) is { } excluded) return Fail($"mot exclu « {excluded} »", out why);
        return true;
    }

    /// <summary>
    /// Un critère renseigné (<paramref name="set"/>) : valeur inconnue -> écartée s'il est obligatoire, gardée sinon ;
    /// valeur connue et non conforme (<paramref name="mismatch"/>) -> écartée. null : rien à redire.
    /// </summary>
    private static bool? Check(SearchProject p, string criterion, bool set, bool unknown, bool mismatch, string reason, string label, out string? why)
    {
        why = null;
        if (!set) return null;
        if (unknown) return p.IsStrict(criterion) ? Fail($"{label} non renseigné", out why) : null;
        return mismatch ? Fail(reason, out why) : null;
    }

    private static bool Fail(string reason, out string? why)
    {
        why = reason;
        return false;
    }
}

/// <summary>
/// Périmètre d'un projet dans le catalogue : une annonce en fait partie si elle respecte ses critères et sa zone,
/// qu'elle vienne d'un lancement du projet ou de la navigation avec le plugin. Rien n'est stocké : le périmètre
/// suit les modifications du projet. Un critère inconnu n'exclut pas l'annonce, mais il faut pouvoir la situer.
/// </summary>
public static class ProjectScope
{
    public static bool Contains(SearchProject p, ListingData d) =>
        ProjectMatcher.Matches(p, d, out _) && InZone(p, d);

    private static bool InZone(SearchProject p, ListingData d)
    {
        if (p.LocationMode != LocationMode.Places)
            return p.Area is { } area && AreaFilter.Check(area, d, out _) == AreaFilter.Verdict.Inside;
        return p.Locations.Any(l => InPlace(l, d));
    }

    private static bool InPlace(ProjectLocation l, ListingData d)
    {
        var cp = d.PostalCode?.Trim();
        var city = d.City is null ? "" : DedupScorer.NormalizeText(d.City);
        return l.Type switch
        {
            PlaceType.PostalCode => cp == (l.Code ?? l.Query),
            PlaceType.Commune => cp is not null && l.PostalCodes.Contains(cp) && (city.Length == 0 || city == DedupScorer.NormalizeText(l.Query)),
            PlaceType.Department => cp is not null && l.Code is not null && DepartmentOf(cp).Contains(l.Code.ToUpperInvariant()),
            PlaceType.Region => cp is not null && DepartmentOf(cp).Intersect(l.DepartmentCodes).Any(),
            _ => cp == l.Query.Trim() || (city.Length > 0 && city == DedupScorer.NormalizeText(l.Query)),
        };
    }

    /// <summary>
    /// Mode lieux : true si le code postal de l'annonce n'appartient à aucun lieu du projet. Sans code postal, ou avec
    /// un lieu en texte libre, on ne peut pas trancher : l'annonce est gardée.
    /// </summary>
    public static bool OutsidePlaces(SearchProject p, ListingData d)
    {
        var cp = d.PostalCode?.Trim();
        if (cp is not { Length: 5 } || p.Locations.Count == 0) return false;
        return !p.Locations.Any(l => l.Type switch
        {
            PlaceType.PostalCode => cp == (l.Code ?? l.Query).Trim(),
            PlaceType.Commune => l.PostalCodes.Count == 0 || (l.PostalCodes.Contains(cp)
                && (SameCommune(l.Query, d.City) || (l.FormerNames?.Any(n => SameCommune(n, d.City)) ?? false))),
            PlaceType.Department => l.Code is null || DepartmentOf(cp).Contains(l.Code.ToUpperInvariant()),
            PlaceType.Region => l.DepartmentCodes.Count == 0 || DepartmentOf(cp).Intersect(l.DepartmentCodes).Any(),
            _ => true,
        });
    }

    /// <summary>
    /// La ville de l'annonce est-elle la commune choisie ? Un code postal couvre souvent plusieurs communes, et un site
    /// peut chercher par code postal (SeLoger) : la ville départage. Ville inconnue : gardée. Accents, casse, tirets
    /// et "St"/"Ste" ignorés ; "Paris 15e" correspond à "Paris".
    /// </summary>
    public static bool SameCommune(string commune, string? city)
    {
        if (string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(commune)) return true;
        var a = CommuneName(commune);
        var b = CommuneName(city);
        return a == b || b.StartsWith(a + " ", StringComparison.Ordinal);
    }

    private static string CommuneName(string name) => string.Join(' ',
        Dedup.DedupScorer.NormalizeText(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w switch { "st" => "saint", "ste" => "sainte", _ => w }));

    /// <summary>Code(s) de département d'un code postal : Corse "20" -> 2A ou 2B, outre-mer sur trois chiffres.</summary>
    public static IReadOnlyList<string> DepartmentOf(string postalCode)
    {
        if (postalCode.Length < 2) return [];
        var two = postalCode[..2];
        if (two == "20") return ["2A", "2B"];
        return two is "97" or "98" && postalCode.Length >= 3 ? [postalCode[..3]] : [two];
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
