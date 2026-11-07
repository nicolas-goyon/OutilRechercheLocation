namespace RechercheLogement.Core.Model;

/// <summary>
/// Équipements communs à tous les sites (clés de <see cref="ListingData.Features"/> et des critères de projet).
/// Chaque connecteur fait la correspondance avec les noms de son site.
/// </summary>
public static class Features
{
    public const string Garden = "garden";
    public const string Pool = "pool";
    public const string Terrace = "terrace";
    public const string Balcony = "balcony";
    public const string Garage = "garage";
    public const string Parking = "parking";
    public const string Cellar = "cellar";
    public const string Elevator = "elevator";
    public const string Fireplace = "fireplace";
    public const string AirConditioning = "airConditioning";
    public const string Accessible = "accessible";

    /// <summary>Équipements proposés dans le formulaire de projet, dans l'ordre d'affichage.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Garden, "Jardin"),
        (Pool, "Piscine"),
        (Terrace, "Terrasse"),
        (Balcony, "Balcon"),
        (Garage, "Garage"),
        (Parking, "Parking"),
        (Cellar, "Cave"),
        (Fireplace, "Cheminée"),
        (Elevator, "Ascenseur"),
        (AirConditioning, "Climatisation"),
        (Accessible, "Accès PMR"),
    ];

    public static string Label(string id) => All.FirstOrDefault(f => f.Id == id).Label ?? id;

    public static bool IsKnown(string id) => All.Any(f => f.Id == id);

    /// <summary>Équipements lus sur une annonce ; les valeurs inconnues (null) sont ignorées. Aucun : null.</summary>
    public static IReadOnlyDictionary<string, bool>? Of(params (string Id, bool? Value)[] values)
    {
        var found = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (id, value) in values)
            if (value is { } v) found[id] = found.TryGetValue(id, out var already) ? already || v : v;
        return found.Count == 0 ? null : found;
    }
}

/// <summary>Classes A à G du DPE (énergie) et du GES (gaz à effet de serre).</summary>
public static class EnergyClasses
{
    public static readonly IReadOnlyList<string> All = ["A", "B", "C", "D", "E", "F", "G"];

    /// <summary>"d", " D ", "Performance énergétique : D" -> "D". Autre chose ("NS", "VI", vide) : null.</summary>
    public static string? Normalize(string? value)
    {
        var t = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(t)) return null;
        if (t.Length > 1 && t.LastIndexOf(':') is var colon and >= 0) t = t[(colon + 1)..].Trim();
        return t.Length == 1 && t[0] is >= 'A' and <= 'G' ? t : null;
    }

    /// <summary>Classes au moins aussi bonnes que <paramref name="max"/> : "C" -> A, B, C.</summary>
    public static IReadOnlyList<string> UpTo(string max) =>
        Normalize(max) is { } m ? All.TakeWhile(c => string.CompareOrdinal(c, m) <= 0).ToList() : All;

    /// <summary><paramref name="value"/> est-elle pire que <paramref name="max"/> ? Inconnue : non.</summary>
    public static bool Worse(string? value, string? max) =>
        Normalize(value) is { } v && Normalize(max) is { } m && string.CompareOrdinal(v, m) > 0;
}

/// <summary>Mode de vente, quand ce n'est pas une vente classique.</summary>
public static class SaleKinds
{
    /// <summary>Viager (bouquet + rente).</summary>
    public const string LifeAnnuity = "life-annuity";
    /// <summary>Vente aux enchères (en ligne ou adjudication).</summary>
    public const string Auction = "auction";
    /// <summary>Appel d'offres (cessions de l'État).</summary>
    public const string Tender = "tender";

    public static string Label(string? kind) => kind switch
    {
        LifeAnnuity => "Viager",
        Auction => "Enchères",
        Tender => "Appel d'offres",
        _ => kind ?? "",
    };
}

/// <summary>
/// Critères de projet qui peuvent être "obligatoires" (<see cref="Projects.SearchProject.StrictCriteria"/>) : une
/// annonce qui ne précise pas l'information est alors écartée. Sinon, seule une valeur précisée et non conforme
/// l'écarte. Les sites écartent toujours les annonces sans l'information quand ils filtrent eux-mêmes : un critère
/// non obligatoire ne leur est donc pas envoyé (sauf si toutes leurs annonces la précisent).
/// </summary>
public static class Criteria
{
    public const string Bedrooms = "bedrooms";
    public const string LandSurface = "landSurface";
    public const string Bathrooms = "bathrooms";
    public const string EnergyClass = "energyClass";
    public const string YearBuilt = "yearBuilt";
    public const string NewBuild = "newBuild";
    public const string WorkNeeded = "workNeeded";
    /// <summary>Équipements exigés : chacun doit être mentionné comme présent.</summary>
    public const string Features = "features";

    public static readonly IReadOnlyList<string> All = [Bedrooms, LandSurface, Bathrooms, EnergyClass, YearBuilt, NewBuild, WorkNeeded, Features];
}
