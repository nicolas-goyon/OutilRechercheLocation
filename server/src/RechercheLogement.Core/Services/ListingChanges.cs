using System.Globalization;
using RechercheLogement.Core.Model;

namespace RechercheLogement.Core.Services;

/// <summary>
/// Changements d'une annonce entre deux lectures, en français, pour l'historique du bien : "prix 250 000 € → 239 000 €
/// (−4,4 %)", "DPE E → D", "ajouté : piscine". Seule une information déjà connue qui change compte : une information
/// qui apparaît (premier passage de l'API après la carte) n'est pas un changement de l'annonce.
/// </summary>
public static class ListingChanges
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static List<string> Describe(ListingData before, ListingData after)
    {
        var changes = new List<string>();
        if (before.Price is { } p0 && after.Price is { } p1 && p0 != p1)
        {
            var pct = p0 == 0 ? "" : $" ({((p1 - p0) / p0 * 100).ToString("+0.#;−0.#", Fr)} %)";
            changes.Add($"prix {Money(p0)} → {Money(p1)}{pct}");
        }
        Compare(changes, "charges", before.Charges, after.Charges, Money);
        Compare(changes, "surface", before.Surface, after.Surface, v => $"{Num(v)} m²");
        Compare(changes, "terrain", before.LandSurface, after.LandSurface, v => $"{Num(v)} m²");
        Compare(changes, "pièces", before.Rooms, after.Rooms, Int);
        Compare(changes, "chambres", before.Bedrooms, after.Bedrooms, Int);
        Compare(changes, "salles de bain", before.Bathrooms, after.Bathrooms, Int);
        Compare(changes, "étage", before.Floor, after.Floor, Int);
        Compare(changes, "DPE", before.EnergyClass, after.EnergyClass, v => v);
        Compare(changes, "GES", before.GhgClass, after.GhgClass, v => v);
        Compare(changes, "taxe foncière", before.PropertyTax, after.PropertyTax, Money);
        Compare(changes, "meublé", before.Furnished, after.Furnished, YesNo);
        Compare(changes, "travaux à prévoir", before.WorkNeeded, after.WorkNeeded, YesNo);
        Compare(changes, "vente", before.SaleKind, after.SaleKind, SaleKinds.Label);
        Compare(changes, "agence", before.AgencyName, after.AgencyName, v => v);

        if (before.Features is { Count: > 0 } && after.Features is { Count: > 0 })
        {
            var added = Features.All.Where(f => before.Has(f.Id) != true && after.Has(f.Id) == true && before.Has(f.Id) is not null).Select(f => f.Label.ToLowerInvariant()).ToList();
            var removed = Features.All.Where(f => before.Has(f.Id) == true && after.Has(f.Id) == false).Select(f => f.Label.ToLowerInvariant()).ToList();
            if (added.Count > 0) changes.Add($"ajouté : {string.Join(", ", added)}");
            if (removed.Count > 0) changes.Add($"retiré : {string.Join(", ", removed)}");
        }
        if (before.Photos is { Count: > 0 } ph0 && after.Photos is { Count: > 0 } ph1 && ph0.Count != ph1.Count)
            changes.Add($"photos {ph0.Count} → {ph1.Count}");
        if (before.DescriptionExcerpt is { } d0 && after.DescriptionExcerpt is { } d1 && d0 != d1)
            changes.Add("description modifiée");
        return changes;
    }

    /// <summary>"Bien'ici : prix 250 000 € → 239 000 € (−4,4 %) · DPE E → D". Aucun changement : null.</summary>
    public static string? EventText(string site, ListingData before, ListingData after) =>
        Describe(before, after) is { Count: > 0 } changes ? $"{Labels.Site(site)} : {string.Join(" · ", changes)}" : null;

    private static void Compare<T>(List<string> changes, string label, T? before, T? after, Func<T, string> format) where T : struct
    {
        if (before is { } b && after is { } a && !EqualityComparer<T>.Default.Equals(a, b)) changes.Add($"{label} {format(b)} → {format(a)}");
    }

    private static void Compare(List<string> changes, string label, string? before, string? after, Func<string, string> format)
    {
        if (before is not null && after is not null && before != after) changes.Add($"{label} {format(before)} → {format(after)}");
    }

    private static string Money(decimal v) => $"{v.ToString("#,0", Fr)} €";
    private static string Num(double v) => v.ToString("#,0.#", Fr);
    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string YesNo(bool v) => v ? "oui" : "non";
}
