namespace RechercheLogement.Core.Model;

/// <summary>
/// Types de biens communs à tous les sites. Chaque connecteur traduit ces types dans les valeurs de son site
/// (recherche), et ses annonces vers ces types (<see cref="Normalize"/>).
/// </summary>
public static class PropertyTypes
{
    public const string Flat = "flat";
    public const string House = "house";
    public const string TownHouse = "townhouse";
    public const string Castle = "castle";
    public const string Loft = "loft";
    public const string Terrain = "terrain";
    public const string Building = "building";
    public const string Commercial = "commercial";
    public const string Parking = "parking";
    public const string Other = "other";

    /// <summary>Types proposés dans le formulaire de projet, dans l'ordre d'affichage.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Flat, "Appartement"),
        (House, "Maison"),
        (TownHouse, "Maison de ville"),
        (Castle, "Château, manoir"),
        (Loft, "Loft, atelier"),
        (Terrain, "Terrain"),
        (Building, "Immeuble"),
        (Commercial, "Local, bureau, commerce"),
        (Parking, "Parking, box"),
        (Other, "Autre (grange, ferme…)"),
    ];

    public static string Label(string id) => All.FirstOrDefault(t => t.Id == Normalize(id)).Label ?? id;

    /// <summary>
    /// Valeur d'un site ou du plugin -> type commun ("APARTMENT", "villa", "shop", "Plot"…). Valeur inconnue : null,
    /// pour ne pas écarter une annonce d'un projet sur un type mal compris.
    /// </summary>
    public static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "flat" or "apartment" or "studio" or "duplex" or "penthouse" => Flat,
        "house" or "villa" or "chalet" or "bungalow" or "farmhouse" or "house_apartment" => House,
        "townhouse" or "town_house" => TownHouse,
        "castle" or "mansion" or "manor" => Castle,
        "loft" => Loft,
        "terrain" or "land" or "plot" => Terrain,
        "building" => Building,
        "commercial" or "shop" or "premises" or "office" or "trading" or "all_commercial" => Commercial,
        "parking" or "garage" or "box" => Parking,
        "other" or "others" or "unknown" or "miscellaneous" or "agriculture_forestry" => Other,
        _ => null,
    };
}
